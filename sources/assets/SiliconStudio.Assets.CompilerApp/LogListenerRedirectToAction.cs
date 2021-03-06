﻿// Copyright (c) 2014 Silicon Studio Corp. (http://siliconstudio.co.jp)
// This file is distributed under GPL v3. See LICENSE.md for details.

using System;

using SiliconStudio.Core.Diagnostics;

namespace SiliconStudio.Assets.CompilerApp
{
    /// <summary>
    /// A log listener redirecting logs to an action
    /// </summary>
    public class LogListenerRedirectToAction : LogListener
    {
        private readonly Action<string, ConsoleColor> logger;

        public LogListenerRedirectToAction(Action<string, ConsoleColor> logger)
        {
            if (logger == null) throw new ArgumentNullException("logger");
            this.logger = logger;
        }

        /// <summary>
        /// Gets or sets the minimum log level handled by this listener.
        /// </summary>
        /// <value>The minimum log level.</value>
        public LogMessageType LogLevel { get; set; }

        protected override void OnLog(ILogMessage logMessage)
        {
            // Always log when debugger is attached
            if (logMessage.Type < LogLevel)
            {
                return;
            }

            var color = ConsoleColor.Gray;

            // set the color depending on the message log level
            switch (logMessage.Type)
            {
                case LogMessageType.Debug:
                    color = ConsoleColor.DarkGray;
                    break;
                case LogMessageType.Verbose:
                    color = ConsoleColor.Gray;
                    break;
                case LogMessageType.Info:
                    color = ConsoleColor.Green;
                    break;
                case LogMessageType.Warning:
                    color = ConsoleColor.Yellow;
                    break;
                case LogMessageType.Error:
                case LogMessageType.Fatal:
                    color = ConsoleColor.Red;
                    break;
            }

            logger(GetDefaultText(logMessage), color);
        }
    }
}