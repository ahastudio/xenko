// Copyright (c) 2014 Silicon Studio Corp. (http://siliconstudio.co.jp)
// This file is distributed under GPL v3. See LICENSE.md for details.
using System;
using System.Collections.Generic;
using System.Linq;
using SiliconStudio.Core.Reflection;
using SiliconStudio.Quantum.References;

namespace SiliconStudio.Quantum.Contents
{
    /// <summary>
    /// A base abstract implementation of the <see cref="IContent"/> interface.
    /// </summary>
    public abstract class ContentBase : GraphNode, IContent
    {
        protected ContentBase(string name, Guid guid, ITypeDescriptor descriptor, bool isPrimitive, IReference reference)
            : base(name, guid)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            Reference = reference;
            Descriptor = descriptor;
            IsPrimitive = isPrimitive;
        }

        /// <inheritdoc/>
        public IContentNode OwnerNode => this;

        /// <inheritdoc/>
        public Type Type => Descriptor.Type;

        /// <inheritdoc/>
        public abstract object Value { get; }

        /// <inheritdoc/>
        public bool IsPrimitive { get; }

        /// <inheritdoc/>
        public ITypeDescriptor Descriptor { get; }

        /// <inheritdoc/>
        public bool IsReference => Reference != null;

        /// <inheritdoc/>
        public IReference Reference { get; }

        /// <inheritdoc/>
        public IEnumerable<Index> Indices => GetIndices();

        /// <inheritdoc/>
        public event EventHandler<ContentChangeEventArgs> PrepareChange;

        /// <inheritdoc/>
        public event EventHandler<ContentChangeEventArgs> FinalizeChange;

        /// <inheritdoc/>
        public event EventHandler<ContentChangeEventArgs> Changing;

        /// <inheritdoc/>
        public event EventHandler<ContentChangeEventArgs> Changed;

        /// <inheritdoc/>
        public virtual object Retrieve()
        {
            return SiliconStudio.Quantum.Contents.Content.Retrieve(Value, Index.Empty, Descriptor);
        }

        /// <inheritdoc/>
        public virtual object Retrieve(Index index)
        {
            return SiliconStudio.Quantum.Contents.Content.Retrieve(Value, index, Descriptor);
        }

        /// <inheritdoc/>
        public virtual void Update(object newValue)
        {
            Update(newValue, Index.Empty);
        }

        /// <inheritdoc/>
        public abstract void Update(object newValue, Index index);

        /// <inheritdoc/>
        public abstract void Add(object newItem);

        /// <inheritdoc/>
        public abstract void Add(object newItem, Index itemIndex);

        /// <inheritdoc/>
        public abstract void Remove(object item, Index itemIndex);

        /// <inheritdoc/>
        public override string ToString()
        {
            return "[" + GetType().Name + "]: " + Value;
        }

        /// <summary>
        /// Updates this content from one of its member.
        /// </summary>
        /// <param name="newValue">The new value for this content.</param>
        /// <param name="index">new index of the value to update.</param>
        /// <remarks>
        /// This method is intended to update a boxed content when one of its member changes.
        /// It allows to properly update boxed structs.
        /// </remarks>
        protected internal abstract void UpdateFromMember(object newValue, Index index);

        /// <summary>
        /// Raises the <see cref="Changing"/> event with the given parameters.
        /// </summary>
        /// <param name="args">The arguments of the event.</param>
        protected void NotifyContentChanging(ContentChangeEventArgs args)
        {
            PrepareChange?.Invoke(this, args);
            Changing?.Invoke(this, args);
        }

        /// <summary>
        /// Raises the <see cref="Changed"/> event with the given arguments.
        /// </summary>
        /// <param name="args">The arguments of the event.</param>
        protected void NotifyContentChanged(ContentChangeEventArgs args)
        {
            Changed?.Invoke(this, args);
            FinalizeChange?.Invoke(this, args);
        }

        private IEnumerable<Index> GetIndices()
        {
            var enumRef = Reference as ReferenceEnumerable;
            if (enumRef != null)
                return enumRef.Indices;

            var collectionDescriptor = Descriptor as CollectionDescriptor;
            if (collectionDescriptor != null)
            {
                return Enumerable.Range(0, collectionDescriptor.GetCollectionCount(Value)).Select(x => new Index(x));
            }
            var dictionaryDescriptor = Descriptor as DictionaryDescriptor;
            return dictionaryDescriptor?.GetKeys(Value).Cast<object>().Select(x => new Index(x));
        }
    }
}
