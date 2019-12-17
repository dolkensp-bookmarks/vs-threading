﻿/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A thread-safe, lazily and asynchronously evaluated value factory.
    /// </summary>
    /// <typeparam name="T">The type of value generated by the value factory.</typeparam>
    public class AsyncLazy<T>
    {
        /// <summary>
        /// The value set to the <see cref="recursiveFactoryCheck"/> field
        /// while the value factory is executing.
        /// </summary>
        private static readonly object RecursiveCheckSentinel = new object();

        /// <summary>
        /// The object to lock to provide thread-safety.
        /// </summary>
        private readonly object syncObject = new object();

        /// <summary>
        /// The unique instance identifier.
        /// </summary>
        private readonly AsyncLocal<object> recursiveFactoryCheck = new AsyncLocal<object>();

        /// <summary>
        /// The function to invoke to produce the task.
        /// </summary>
        private Func<Task<T>>? valueFactory;

        /// <summary>
        /// The async pump to Join on calls to <see cref="GetValueAsync(CancellationToken)"/>.
        /// </summary>
        private JoinableTaskFactory? jobFactory;

        /// <summary>
        /// The result of the value factory.
        /// </summary>
        private Task<T>? value;

        /// <summary>
        /// A joinable task whose result is the value to be cached.
        /// </summary>
        private JoinableTask<T>? joinableTask;

        /// <summary>
        /// Initializes a new instance of the <see cref="AsyncLazy{T}"/> class.
        /// </summary>
        /// <param name="valueFactory">The async function that produces the value.  To be invoked at most once.</param>
        /// <param name="joinableTaskFactory">The factory to use when invoking the value factory in <see cref="GetValueAsync(CancellationToken)"/> to avoid deadlocks when the main thread is required by the value factory.</param>
        public AsyncLazy(Func<Task<T>> valueFactory, JoinableTaskFactory? joinableTaskFactory = null)
        {
            Requires.NotNull(valueFactory, nameof(valueFactory));
            this.valueFactory = valueFactory;
            this.jobFactory = joinableTaskFactory;
        }

        /// <summary>
        /// Gets a value indicating whether the value factory has been invoked.
        /// </summary>
        public bool IsValueCreated
        {
            get
            {
                Interlocked.MemoryBarrier();
                return this.valueFactory == null;
            }
        }

        /// <summary>
        /// Gets a value indicating whether the value factory has been invoked and has run to completion.
        /// </summary>
        public bool IsValueFactoryCompleted
        {
            get
            {
                Interlocked.MemoryBarrier();
                return this.value != null && this.value.IsCompleted;
            }
        }

        /// <summary>
        /// Gets the task that produces or has produced the value.
        /// </summary>
        /// <returns>A task whose result is the lazily constructed value.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the value factory calls <see cref="GetValueAsync()"/> on this instance.
        /// </exception>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate")]
        public Task<T> GetValueAsync() => this.GetValueAsync(CancellationToken.None);

        /// <summary>
        /// Gets the task that produces or has produced the value.
        /// </summary>
        /// <param name="cancellationToken">
        /// A token whose cancellation indicates that the caller no longer is interested in the result.
        /// Note that this will not cancel the value factory (since other callers may exist).
        /// But this token will result in an expediant cancellation of the returned Task,
        /// and a dis-joining of any <see cref="JoinableTask"/> that may have occurred as a result of this call.
        /// </param>
        /// <returns>A task whose result is the lazily constructed value.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the value factory calls <see cref="GetValueAsync()"/> on this instance.
        /// </exception>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        public Task<T> GetValueAsync(CancellationToken cancellationToken)
        {
            if (!((this.value != null && this.value.IsCompleted) || this.recursiveFactoryCheck.Value == null))
            {
                // PERF: we check the condition and *then* retrieve the string resource only on failure
                // because the string retrieval has shown up as significant on ETL traces.
                Verify.FailOperation(Strings.ValueFactoryReentrancy);
            }

            if (this.value == null)
            {
                if (Monitor.IsEntered(this.syncObject))
                {
                    // PERF: we check the condition and *then* retrieve the string resource only on failure
                    // because the string retrieval has shown up as significant on ETL traces.
                    Verify.FailOperation(Strings.ValueFactoryReentrancy);
                }

                InlineResumable? resumableAwaiter = null;
                lock (this.syncObject)
                {
                    // Note that if multiple threads hit GetValueAsync() before
                    // the valueFactory has completed its synchronous execution,
                    // then only one thread will execute the valueFactory while the
                    // other threads synchronously block till the synchronous portion
                    // has completed.
                    if (this.value == null)
                    {
                        RoslynDebug.Assert(this.valueFactory is object);

                        cancellationToken.ThrowIfCancellationRequested();
                        resumableAwaiter = new InlineResumable();
                        Func<Task<T>>? originalValueFactory = this.valueFactory;
                        this.valueFactory = null;
                        Func<Task<T>> valueFactory = async delegate
                        {
                            try
                            {
                                await resumableAwaiter;
                                return await originalValueFactory().ConfigureAwaitRunInline();
                            }
                            finally
                            {
                                this.jobFactory = null;
                                this.joinableTask = null;
                            }
                        };

                        this.recursiveFactoryCheck.Value = RecursiveCheckSentinel;
                        try
                        {
                            if (this.jobFactory != null)
                            {
                                // Wrapping with RunAsync allows a future caller
                                // to synchronously block the Main thread waiting for the result
                                // without leading to deadlocks.
                                this.joinableTask = this.jobFactory.RunAsync(valueFactory);
                                this.value = this.joinableTask.Task;
                            }
                            else
                            {
                                this.value = valueFactory();
                            }
                        }
                        finally
                        {
                            this.recursiveFactoryCheck.Value = null;
                        }
                    }
                }

                // Allow the original value factory to actually run.
                resumableAwaiter?.Resume();
            }

            if (!this.value.IsCompleted)
            {
                this.joinableTask?.JoinAsync(cancellationToken).Forget();
            }

            return this.value.WithCancellation(cancellationToken);
        }

        /// <summary>
        /// Gets the lazily computed value.
        /// </summary>
        /// <returns>The lazily constructed value.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the value factory calls <see cref="GetValueAsync()"/> on this instance.
        /// </exception>
        public T GetValue() => this.GetValue(CancellationToken.None);

        /// <summary>
        /// Gets the lazily computed value.
        /// </summary>
        /// <param name="cancellationToken">
        /// A token whose cancellation indicates that the caller no longer is interested in the result.
        /// Note that this will not cancel the value factory (since other callers may exist).
        /// But when this token is canceled, the caller will experience an <see cref="OperationCanceledException"/>
        /// immediately and a dis-joining of any <see cref="JoinableTask"/> that may have occurred as a result of this call.
        /// </param>
        /// <returns>The lazily constructed value.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the value factory calls <see cref="GetValueAsync()"/> on this instance.
        /// </exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled before the value is computed.</exception>
        public T GetValue(CancellationToken cancellationToken)
        {
            // As a perf optimization, avoid calling JTF or GetValueAsync if the value factory has already completed.
            if (this.IsValueFactoryCompleted)
            {
                RoslynDebug.Assert(this.value is object);

                return this.value.GetAwaiter().GetResult();
            }
            else
            {
                // Capture the factory as a local before comparing and dereferencing it since
                // the field can transition to null and we want to gracefully handle that race condition.
                JoinableTaskFactory? factory = this.jobFactory;
                return factory != null
                    ? factory.Run(() => this.GetValueAsync(cancellationToken))
                    : this.GetValueAsync(cancellationToken).GetAwaiter().GetResult();
            }
        }

        /// <summary>
        /// Renders a string describing an uncreated value, or the string representation of the created value.
        /// </summary>
        public override string ToString()
        {
            return (this.value != null && this.value.IsCompleted)
                ? (this.value.Status == TaskStatus.RanToCompletion ? $"{this.value.Result}" : Strings.LazyValueFaulted)
                : Strings.LazyValueNotCreated;
        }
    }
}
