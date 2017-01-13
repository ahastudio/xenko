﻿using System;
using System.Collections.Generic;
using System.Linq;
using SiliconStudio.Core.Annotations;
using SiliconStudio.Core.Collections;
using SiliconStudio.Core.Reflection;
using SiliconStudio.Core.Yaml;
using SiliconStudio.Quantum;
using SiliconStudio.Quantum.Contents;

namespace SiliconStudio.Assets.Quantum
{
    public class AssetObjectNode : AssetNode
    {
        public AssetObjectNode(string name, IContent content, Guid guid) : base(name, content, guid)
        {
        }
    }

    public class AssetBoxedNode : AssetNode
    {
        public AssetBoxedNode(string name, IContent content, Guid guid) : base(name, content, guid)
        {
        }
    }

    public abstract class AssetNode : GraphNode
    {
        private AssetPropertyGraph propertyGraph;
        private readonly Dictionary<string, IContent> contents = new Dictionary<string, IContent>();

        protected AssetNode(string name, IContent content, Guid guid)
            : base(name, content, guid)
        {
        }

        public sealed override IContent Content => base.Content;

        public AssetPropertyGraph PropertyGraph { get { return propertyGraph; } internal set { if (value == null) throw new ArgumentNullException(nameof(value)); propertyGraph = value; } }

        public IContent BaseContent { get; private set; }

        public bool HasContent(string key)
        {
            return contents.ContainsKey(key);
        }

        public void SetContent(string key, IContent content)
        {
            contents[key] = content;
        }

        public IContent GetContent(string key)
        {
            IContent content;
            contents.TryGetValue(key, out content);
            return content;
        }

        /// <summary>
        /// Clones the given object, remove any override information on it, and propagate its id (from <see cref="IdentifiableHelper"/>) to the cloned object.
        /// </summary>
        /// <param name="value">The object to clone.</param>
        /// <returns>A clone of the given object.</returns>
        /// <remarks>If the given object is null, this method returns null.</remarks>
        /// <remarks>If the given object is a content reference, the given object won't be cloned but directly returned.</remarks>
        public static object CloneFromBase(object value)
        {
            if (value == null)
                return null;

            // TODO: check if the cloner is aware of the content type (attached reference) and does not already avoid cloning them.

            // TODO FIXME
            //if (SessionViewModel.Instance.ContentReferenceService.IsContentType(value.GetType()))
            //    return value;

            var result = AssetCloner.Clone(value);
            return result;
        }

        internal void SetBase(IContent baseContent)
        {
            BaseContent = baseContent;
        }

        /// <summary>
        /// Resets the overrides attached to this node and its descendants, recursively.
        /// </summary>
        /// <param name="indexToReset">The index of the override to reset in this node, if relevant.</param>
        public virtual void ResetOverride(Index indexToReset)
        {
            var visitor = PropertyGraph.CreateReconcilierVisitor();
            visitor.SkipRootNode = true;
            visitor.Visiting += (node, path) =>
            {
                var childNode = node as AssetMemberNode;
                if (childNode == null)
                    return;

                childNode.OverrideContent(false);
                foreach (var overrideItem in childNode.GetOverriddenItemIndices())
                {
                    childNode.OverrideItem(false, overrideItem);
                }
                foreach (var overrideKey in childNode.GetOverriddenKeyIndices())
                {
                    childNode.OverrideKey(false, overrideKey);
                }
            };
            visitor.Visit(this);

            PropertyGraph.ReconcileWithBase(this);
        }

        public static AssetNode ResolveObjectPath(AssetNode rootNode, YamlAssetPath path, out Index index, out bool overrideOnKey)
        {
            var currentNode = rootNode;
            index = Index.Empty;
            overrideOnKey = false;
            for (var i = 0; i < path.Items.Count; i++)
            {
                var item = path.Items[i];
                switch (item.Type)
                {
                    case YamlAssetPath.ItemType.Member:
                        index = Index.Empty;
                        overrideOnKey = false;
                        if (currentNode.Content.IsReference)
                        {
                            currentNode = (AssetNode)((IGraphNode)currentNode).Target;
                        }
                        string name = item.AsMember();
                        currentNode = (AssetNode)((IGraphNode)currentNode).TryGetChild(name);
                        break;
                    case YamlAssetPath.ItemType.Index:
                        index = new Index(item.Value);
                        overrideOnKey = true;
                        if (currentNode.Content.IsReference && i < path.Items.Count - 1)
                        {
                            Index index1 = new Index(item.Value);
                            currentNode = (AssetNode)((IGraphNode)currentNode).IndexedTarget(index1);
                        }
                        break;
                    case YamlAssetPath.ItemType.ItemId:
                        var ids = CollectionItemIdHelper.GetCollectionItemIds(currentNode.Content.Retrieve());
                        var key = ids.GetKey(item.AsItemId());
                        index = new Index(key);
                        overrideOnKey = false;
                        if (currentNode.Content.IsReference && i < path.Items.Count - 1)
                        {
                            Index index1 = new Index(key);
                            currentNode = (AssetNode)((IGraphNode)currentNode).IndexedTarget(index1);
                        }
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                // Something wrong happen, the node is unreachable.
                if (currentNode == null)
                    return null;
            }

            return currentNode;
        }
    }

    public class AssetMemberNode : AssetNode
    {
        internal bool contentUpdating;
        private OverrideType contentOverride;
        private readonly Dictionary<ItemId, OverrideType> itemOverrides = new Dictionary<ItemId, OverrideType>();
        private readonly Dictionary<ItemId, OverrideType> keyOverrides = new Dictionary<ItemId, OverrideType>();
        private CollectionItemIdentifiers collectionItemIdentifiers;
        private ItemId restoringId;

        public AssetMemberNode(string name, IContent content, Guid guid) : base(name, content, guid)
        {
            Content.PrepareChange += (sender, e) => contentUpdating = true;
            Content.FinalizeChange += (sender, e) => contentUpdating = false;
            Content.Changed += ContentChanged;
            IsNonIdentifiableCollectionContent = (Content as MemberContent)?.Member.GetCustomAttributes<NonIdentifiableCollectionItemsAttribute>(true)?.Any() ?? false;
            CanOverride = (Content as MemberContent)?.Member.GetCustomAttributes<NonOverridableAttribute>(true)?.Any() != true;
        }

        public bool IsNonIdentifiableCollectionContent { get; }

        public bool CanOverride { get; }

        internal bool ResettingOverride { get; set; }

        public event EventHandler<EventArgs> OverrideChanging;

        public event EventHandler<EventArgs> OverrideChanged;

        public void OverrideContent(bool isOverridden)
        {
            if (CanOverride)
            {
                OverrideChanging?.Invoke(this, EventArgs.Empty);
                contentOverride = isOverridden ? OverrideType.New : OverrideType.Base;
                OverrideChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void OverrideItem(bool isOverridden, Index index)
        {
            if (CanOverride)
            {
                OverrideChanging?.Invoke(this, EventArgs.Empty);
                SetItemOverride(isOverridden ? OverrideType.New : OverrideType.Base, index);
                OverrideChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void OverrideKey(bool isOverridden, Index index)
        {
            if (CanOverride)
            {
                OverrideChanging?.Invoke(this, EventArgs.Empty);
                SetKeyOverride(isOverridden ? OverrideType.New : OverrideType.Base, index);
                OverrideChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void OverrideDeletedItem(bool isOverridden, ItemId deletedId)
        {
            CollectionItemIdentifiers ids;
            if (CanOverride && TryGetCollectionItemIds(Content.Retrieve(), out ids))
            {
                OverrideChanging?.Invoke(this, EventArgs.Empty);
                SetOverride(isOverridden ? OverrideType.New : OverrideType.Base, deletedId, itemOverrides);
                if (isOverridden)
                {
                    ids.MarkAsDeleted(deletedId);
                }
                else
                {
                    ids.UnmarkAsDeleted(deletedId);
                }
                OverrideChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public bool IsItemDeleted(ItemId itemId)
        {
            var collection = Content.Retrieve();
            CollectionItemIdentifiers ids;
            if (!TryGetCollectionItemIds(collection, out ids))
                throw new InvalidOperationException("No Collection item identifier associated to the given collection.");
            return ids.IsDeleted(itemId);
        }

        public bool TryGetCollectionItemIds(object instance, out CollectionItemIdentifiers itemIds)
        {
            if (collectionItemIdentifiers != null)
            {
                itemIds = collectionItemIdentifiers;
                return true;
            }

            var result = CollectionItemIdHelper.TryGetCollectionItemIds(instance, out collectionItemIdentifiers);
            itemIds = collectionItemIdentifiers;
            return result;
        }

        public void Restore(object restoredItem, ItemId id)
        {
            CollectionItemIdentifiers oldIds = null;
            CollectionItemIdentifiers ids;
            if (!IsNonIdentifiableCollectionContent && TryGetCollectionItemIds(Content.Retrieve(), out ids))
            {
                // Remove the item from deleted ids if it was here.
                ids.UnmarkAsDeleted(id);
                // Get a clone of the CollectionItemIdentifiers before we add back the item.
                oldIds = new CollectionItemIdentifiers();
                ids.CloneInto(oldIds, null);
            }
            // Actually restore the item.
            Content.Add(restoredItem);

            if (TryGetCollectionItemIds(Content.Retrieve(), out ids) && oldIds != null)
            {
                // Find the new id that has been generated by the Add
                var idToReplace = oldIds.FindMissingId(ids);
                if (idToReplace == ItemId.Empty)
                    throw new InvalidOperationException("No ItemId to replace has been generated.");
            }
        }

        public void Restore(object restoredItem, Index index, ItemId id)
        {
            restoringId = id;
            Content.Add(restoredItem, index);
            restoringId = ItemId.Empty;
            CollectionItemIdentifiers ids;
            if (TryGetCollectionItemIds(Content.Retrieve(), out ids))
            {
                // Remove the item from deleted ids if it was here.
                ids.UnmarkAsDeleted(id);
            }
        }

        public void RemoveAndDiscard(object item, Index itemIndex, ItemId id)
        {
            Content.Remove(item, itemIndex);
            CollectionItemIdentifiers ids;
            if (TryGetCollectionItemIds(Content.Retrieve(), out ids))
            {
                // Remove the item from deleted ids if it was here.
                ids.UnmarkAsDeleted(id);
            }
        }

        internal bool HasId(ItemId id)
        {
            Index index;
            return TryIdToIndex(id, out index);
        }

        internal Index IdToIndex(ItemId id)
        {
            Index index;
            if (!TryIdToIndex(id, out index)) throw new InvalidOperationException("No Collection item identifier associated to the given collection.");
            return index;
        }

        internal bool TryIdToIndex(ItemId id, out Index index)
        {
            if (id == ItemId.Empty)
            {
                index = Index.Empty;
                return true;
            }

            var collection = Content.Retrieve();
            CollectionItemIdentifiers ids;
            if (TryGetCollectionItemIds(collection, out ids))
            {
                index = new Index(ids.GetKey(id));
                return !index.IsEmpty;
            }
            index = Index.Empty;
            return false;

        }

        internal ItemId IndexToId(Index index)
        {
            ItemId id;
            if (!TryIndexToId(index, out id)) throw new InvalidOperationException("No Collection item identifier associated to the given collection.");
            return id;
        }

        public bool TryIndexToId(Index index, out ItemId id)
        {
            if (index == Index.Empty)
            {
                id = ItemId.Empty;
                return true;
            }

            var collection = Content.Retrieve();
            CollectionItemIdentifiers ids;
            if (TryGetCollectionItemIds(collection, out ids))
            {
                return ids.TryGet(index.Value, out id);
            }
            id = ItemId.Empty;
            return false;
        }

        public override void ResetOverride(Index indexToReset)
        {
            if (indexToReset.IsEmpty)
            {
                OverrideContent(false);
            }
            else
            {
                OverrideItem(false, indexToReset);
            }
            base.ResetOverride(indexToReset);
        }

        private void ContentChanged(object sender, ContentChangeEventArgs e)
        {
            // Make sure that we have item ids everywhere we're supposed to.
            AssetCollectionItemIdHelper.GenerateMissingItemIds(e.Content.Retrieve());

            var node = (AssetMemberNode)e.Content.OwnerNode;
            if (node.IsNonIdentifiableCollectionContent)
                return;

            // Create new ids for collection items
            var baseNode = (AssetMemberNode)BaseContent?.OwnerNode;
            var isOverriding = !baseNode?.contentUpdating == true;
            var removedId = ItemId.Empty;
            switch (e.ChangeType)
            {
                case ContentChangeType.ValueChange:
                    break;
                case ContentChangeType.CollectionAdd:
                    {
                        var collectionDescriptor = e.Content.Descriptor as CollectionDescriptor;
                        var itemIds = CollectionItemIdHelper.GetCollectionItemIds(e.Content.Retrieve());
                        // Compute the id we will add for this item
                        ItemId itemId;
                        if (baseNode?.contentUpdating == true)
                        {
                            var baseCollection = baseNode.Content.Retrieve();
                            var baseIds = CollectionItemIdHelper.GetCollectionItemIds(baseCollection);
                            itemId = itemIds.FindMissingId(baseIds);
                        }
                        else
                        {
                            itemId = restoringId != ItemId.Empty ? restoringId : ItemId.New();
                        }
                        // Add the id to the proper location (insert or add)
                        if (collectionDescriptor != null)
                        {
                            if (e.Index != Index.Empty)
                            {
                                itemIds.Insert(e.Index.Int, itemId);
                            }
                            else
                            {
                                throw new InvalidOperationException("An item has been added to a collection that does not have a predictable Add. Consider using NonIdentifiableCollectionItemsAttribute on this collection.");
                            }
                        }
                        else
                        {
                            itemIds[e.Index.Value] = itemId;
                        }
                    }
                    break;
                case ContentChangeType.CollectionRemove:
                    {
                        var collectionDescriptor = e.Content.Descriptor as CollectionDescriptor;
                        if (collectionDescriptor != null)
                        {
                            var itemIds = CollectionItemIdHelper.GetCollectionItemIds(e.Content.Retrieve());
                            removedId = itemIds.DeleteAndShift(e.Index.Int, isOverriding);
                        }
                        else
                        {
                            var itemIds = CollectionItemIdHelper.GetCollectionItemIds(e.Content.Retrieve());
                            removedId = itemIds.Delete(e.Index.Value, isOverriding);
                        }
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }


            // Don't update override if propagation from base is disabled.
            if (PropertyGraph?.Container?.PropagateChangesFromBase == false)
                return;

            // Mark it as New if it does not come from the base
            if (!baseNode?.contentUpdating == true && !ResettingOverride)
            {
                if (e.ChangeType != ContentChangeType.CollectionRemove)
                {
                    if (e.Index == Index.Empty)
                    {
                        OverrideContent(!ResettingOverride);
                    }
                    else
                    {
                        OverrideItem(!ResettingOverride, e.Index);
                    }
                }
                else
                {
                    OverrideDeletedItem(true, removedId);
                }
            }
        }

        internal void SetContentOverride(OverrideType overrideType)
        {
            if (CanOverride)
            {
                contentOverride = overrideType;
            }
        }

        internal void SetItemOverride(OverrideType overrideType, Index index)
        {
            if (CanOverride)
            {
                var id = IndexToId(index);
                SetOverride(overrideType, id, itemOverrides);
            }
        }

        internal void SetKeyOverride(OverrideType overrideType, Index index)
        {
            if (CanOverride)
            {
                var id = IndexToId(index);
                SetOverride(overrideType, id, keyOverrides);
            }
        }

        private static void SetOverride(OverrideType overrideType, ItemId id, Dictionary<ItemId, OverrideType> dictionary)
        {
            if (overrideType == OverrideType.Base)
            {
                dictionary.Remove(id);
            }
            else
            {
                dictionary[id] = overrideType;
            }
        }

        public OverrideType GetContentOverride()
        {
            return contentOverride;
        }

        public OverrideType GetItemOverride(Index index)
        {
            var result = OverrideType.Base;
            ItemId id;
            if (!TryIndexToId(index, out id))
                return result;
            return itemOverrides.TryGetValue(id, out result) ? result : OverrideType.Base;
        }

        public OverrideType GetKeyOverride(Index index)
        {
            var result = OverrideType.Base;
            ItemId id;
            if (!TryIndexToId(index, out id))
                return result;
            return keyOverrides.TryGetValue(id, out result) ? result : OverrideType.Base;
        }

        public bool IsContentOverridden()
        {
            return (contentOverride & OverrideType.New) == OverrideType.New;
        }

        public bool IsItemOverridden(Index index)
        {
            OverrideType result;
            ItemId id;
            if (!TryIndexToId(index, out id))
                return false;
            return itemOverrides.TryGetValue(id, out result) && (result & OverrideType.New) == OverrideType.New;
        }

        public bool IsItemOverriddenDeleted(ItemId id)
        {
            OverrideType result;
            return IsItemDeleted(id) && itemOverrides.TryGetValue(id, out result) && (result & OverrideType.New) == OverrideType.New;
        }

        public bool IsKeyOverridden(Index index)
        {
            OverrideType result;
            ItemId id;
            if (!TryIndexToId(index, out id))
                return false;
            return keyOverrides.TryGetValue(id, out result) && (result & OverrideType.New) == OverrideType.New;
        }

        public bool IsContentInherited()
        {
            return BaseContent != null && !IsContentOverridden();
        }

        public bool IsItemInherited(Index index)
        {
            return BaseContent != null && !IsItemOverridden(index);
        }

        public bool IsKeyInherited(Index index)
        {
            return BaseContent != null && !IsKeyOverridden(index);
        }

        public IEnumerable<Index> GetOverriddenItemIndices()
        {
            if (BaseContent == null)
                yield break;

            CollectionItemIdentifiers ids;
            var collection = Content.Retrieve();
            TryGetCollectionItemIds(collection, out ids);

            foreach (var flags in itemOverrides)
            {
                if ((flags.Value & OverrideType.New) == OverrideType.New)
                {
                    // If the override is a deleted item, there's no matching index to return.
                    if (ids.IsDeleted(flags.Key))
                        continue;

                    yield return IdToIndex(flags.Key);
                }
            }
        }

        public IEnumerable<Index> GetOverriddenKeyIndices()
        {
            if (BaseContent == null)
                yield break;

            CollectionItemIdentifiers ids;
            var collection = Content.Retrieve();
            TryGetCollectionItemIds(collection, out ids);

            foreach (var flags in keyOverrides)
            {
                if ((flags.Value & OverrideType.New) == OverrideType.New)
                {
                    // If the override is a deleted item, there's no matching index to return.
                    if (ids.IsDeleted(flags.Key))
                        continue;

                    yield return IdToIndex(flags.Key);
                }
            }
        }

        internal Dictionary<ItemId, OverrideType> GetAllOverrides()
        {
            return itemOverrides;
        }

        // TODO: move this in AssetPropertyGraph as a private method, it's the only usage (could also be inlined or split in 3 methods)
        internal Index RetrieveDerivedIndex(Index baseIndex, ContentChangeType changeType)
        {
            var baseMemberContent = BaseContent as MemberContent;
            if (baseMemberContent == null)
                return Index.Empty;

            switch (changeType)
            {
                case ContentChangeType.ValueChange:
                    {
                        if (baseIndex.IsEmpty)
                            return baseIndex;

                        var baseNode = (AssetMemberNode)BaseContent.OwnerNode;
                        ItemId baseId;
                        if (!baseNode.TryIndexToId(baseIndex, out baseId))
                            return Index.Empty;

                        Index index;
                        // Find the index of the item in this instance corresponding to the modified item in the base.
                        return TryIdToIndex(baseId, out index) ? index : Index.Empty;
                    }
                case ContentChangeType.CollectionAdd:
                    {
                        if (baseIndex.IsEmpty)
                            return Index.Empty;

                        var baseNode = (AssetMemberNode)BaseContent.OwnerNode;
                        ItemId baseId;
                        if (!baseNode.TryIndexToId(baseIndex, out baseId))
                            throw new InvalidOperationException("Cannot find an identifier matching the index in the base collection");

                        if (BaseContent.Descriptor is CollectionDescriptor)
                        {
                            var currentBaseIndex = baseIndex.Int - 1;
                            // Find the first item before the new one that also exists (in term of id) in the local node
                            while (currentBaseIndex >= 0)
                            {
                                if (!baseNode.TryIndexToId(new Index(currentBaseIndex), out baseId))
                                    throw new InvalidOperationException("Cannot find an identifier matching the index in the base collection");

                                Index localIndex;
                                // If we have an matching item, we want to insert right after it
                                if (TryIdToIndex(baseId, out localIndex))
                                    return new Index(localIndex.Int + 1);

                                currentBaseIndex--;
                            }
                            // Otherwise, insert at 0
                            return new Index(0);
                        }
                        return baseIndex;
                    }
                case ContentChangeType.CollectionRemove:
                    {
                        // If we're removing, we need to find the item id that still exists in our instance but not in the base anymore.
                        var baseIds = CollectionItemIdHelper.GetCollectionItemIds(baseMemberContent.Retrieve());
                        var instanceIds = CollectionItemIdHelper.GetCollectionItemIds(Content.Retrieve());
                        var missingIds = baseIds.FindMissingIds(instanceIds);
                        var foundUnique = false;
                        var index = Index.Empty;
                        foreach (var id in missingIds)
                        {
                            if (TryIdToIndex(id, out index))
                            {
                                if (foundUnique)
                                    throw new InvalidOperationException("Couldn't find a unique item id in the instance collection corresponding to the item removed in the base collection");
                                foundUnique = true;
                            }
                        }
                        return index;
                    }
                default:
                    throw new ArgumentException(@"Cannot retrieve index in derived asset for a remove operation.", nameof(changeType));
            }
        }
    }
}
