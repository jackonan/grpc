#region Copyright notice and license

// Copyright 2015, Google Inc.
// All rights reserved.
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are
// met:
//
//     * Redistributions of source code must retain the above copyright
// notice, this list of conditions and the following disclaimer.
//     * Redistributions in binary form must reproduce the above
// copyright notice, this list of conditions and the following disclaimer
// in the documentation and/or other materials provided with the
// distribution.
//     * Neither the name of Google Inc. nor the names of its
// contributors may be used to endorse or promote products derived from
// this software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
// A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
// OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
// SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
// LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
// DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
// THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Grpc.Core.Internal;
using Grpc.Core.Logging;
using Grpc.Core.Utils;

namespace Grpc.Core
{
    /// <summary>
    /// Encapsulates initialization and shutdown of gRPC library.
    /// </summary>
    public class GrpcEnvironment
    {
        const int MinDefaultThreadPoolSize = 4;

        static object staticLock = new object();
        static GrpcEnvironment instance;
        static int refCount;
        static int? customThreadPoolSize;
        static int? customCompletionQueueCount;

        static ILogger logger = new ConsoleLogger();

        readonly GrpcThreadPool threadPool;
        readonly DebugStats debugStats = new DebugStats();
        readonly AtomicCounter cqPickerCounter = new AtomicCounter();
        bool isClosed;

        /// <summary>
        /// Returns a reference-counted instance of initialized gRPC environment.
        /// Subsequent invocations return the same instance unless reference count has dropped to zero previously.
        /// </summary>
        internal static GrpcEnvironment AddRef()
        {
            lock (staticLock)
            {
                refCount++;
                if (instance == null)
                {
                    instance = new GrpcEnvironment();
                }
                return instance;
            }
        }

        /// <summary>
        /// Decrements the reference count for currently active environment and shuts down the gRPC environment if reference count drops to zero.
        /// (and blocks until the environment has been fully shutdown).
        /// </summary>
        internal static void Release()
        {
            lock (staticLock)
            {
                GrpcPreconditions.CheckState(refCount > 0);
                refCount--;
                if (refCount == 0)
                {
                    instance.Close();
                    instance = null;
                }
            }
        }

        internal static int GetRefCount()
        {
            lock (staticLock)
            {
                return refCount;
            }
        }

        /// <summary>
        /// Gets application-wide logger used by gRPC.
        /// </summary>
        /// <value>The logger.</value>
        public static ILogger Logger
        {
            get
            {
                return logger;
            }
        }

        /// <summary>
        /// Sets the application-wide logger that should be used by gRPC.
        /// </summary>
        public static void SetLogger(ILogger customLogger)
        {
            GrpcPreconditions.CheckNotNull(customLogger, "customLogger");
            logger = customLogger;
        }

        /// <summary>
        /// Sets the number of threads in the gRPC thread pool that polls for internal RPC events.
        /// Can be only invoke before the <c>GrpcEnviroment</c> is started and cannot be changed afterwards.
        /// Setting thread pool size is an advanced setting and you should only use it if you know what you are doing.
        /// Most users should rely on the default value provided by gRPC library.
        /// Note: this method is part of an experimental API that can change or be removed without any prior notice.
        /// </summary>
        public static void SetThreadPoolSize(int threadCount)
        {
            lock (staticLock)
            {
                GrpcPreconditions.CheckState(instance == null, "Can only be set before GrpcEnvironment is initialized");
                GrpcPreconditions.CheckArgument(threadCount > 0, "threadCount needs to be a positive number");
                customThreadPoolSize = threadCount;
            }
        }

        /// <summary>
        /// Sets the number of completion queues in the  gRPC thread pool that polls for internal RPC events.
        /// Can be only invoke before the <c>GrpcEnviroment</c> is started and cannot be changed afterwards.
        /// Setting the number of completions queues is an advanced setting and you should only use it if you know what you are doing.
        /// Most users should rely on the default value provided by gRPC library.
        /// Note: this method is part of an experimental API that can change or be removed without any prior notice.
        /// </summary>
        public static void SetCompletionQueueCount(int completionQueueCount)
        {
            lock (staticLock)
            {
                GrpcPreconditions.CheckState(instance == null, "Can only be set before GrpcEnvironment is initialized");
                GrpcPreconditions.CheckArgument(completionQueueCount > 0, "threadCount needs to be a positive number");
                customCompletionQueueCount = completionQueueCount;
            }
        }

        /// <summary>
        /// Creates gRPC environment.
        /// </summary>
        private GrpcEnvironment()
        {
            GrpcNativeInit();
            threadPool = new GrpcThreadPool(this, GetThreadPoolSizeOrDefault(), GetCompletionQueueCountOrDefault());
            threadPool.Start();
        }

        /// <summary>
        /// Gets the completion queues used by this gRPC environment.
        /// </summary>
        internal IReadOnlyCollection<CompletionQueueSafeHandle> CompletionQueues
        {
            get
            {
                return this.threadPool.CompletionQueues;
            }
        }

        /// <summary>
        /// Picks a completion queue in a round-robin fashion.
        /// Shouldn't be invoked on a per-call basis (used at per-channel basis).
        /// </summary>
        internal CompletionQueueSafeHandle PickCompletionQueue()
        {
            var cqIndex = (int) ((cqPickerCounter.Increment() - 1) % this.threadPool.CompletionQueues.Count);
            return this.threadPool.CompletionQueues.ElementAt(cqIndex);
        }

        /// <summary>
        /// Gets the completion queue used by this gRPC environment.
        /// </summary>
        internal DebugStats DebugStats
        {
            get
            {
                return this.debugStats;
            }
        }

        /// <summary>
        /// Gets version of gRPC C core.
        /// </summary>
        internal static string GetCoreVersionString()
        {
            var ptr = NativeMethods.Get().grpcsharp_version_string();  // the pointer is not owned
            return Marshal.PtrToStringAnsi(ptr);
        }

        internal static void GrpcNativeInit()
        {
            NativeMethods.Get().grpcsharp_init();
        }

        internal static void GrpcNativeShutdown()
        {
            NativeMethods.Get().grpcsharp_shutdown();
        }

        /// <summary>
        /// Shuts down this environment.
        /// </summary>
        private void Close()
        {
            if (isClosed)
            {
                throw new InvalidOperationException("Close has already been called");
            }
            threadPool.Stop();
            GrpcNativeShutdown();
            isClosed = true;

            debugStats.CheckOK();
        }

        private int GetThreadPoolSizeOrDefault()
        {
            if (customThreadPoolSize.HasValue)
            {
                return customThreadPoolSize.Value;
            }
            // In systems with many cores, use half of the cores for GrpcThreadPool
            // and the other half for .NET thread pool. This heuristic definitely needs
            // more work, but seems to work reasonably well for a start.
            return Math.Max(MinDefaultThreadPoolSize, Environment.ProcessorCount / 2);
        }

        private int GetCompletionQueueCountOrDefault()
        {
            if (customCompletionQueueCount.HasValue)
            {
                return customCompletionQueueCount.Value;
            }
            // by default, create a completion queue for each thread
            return GetThreadPoolSizeOrDefault();
        }
    }
}
