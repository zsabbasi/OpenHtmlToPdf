using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Pechkin.Util;

namespace Pechkin
{
    /// <summary>
    /// This class runs the thread and lets users to run delegates synchronously on that thread while obtaining results of the execution.
    /// 
    /// It's like <code>ISynchronizedInvoke</code>, but with only synchronous methods (because we don't need more).
    /// </summary>
    internal static class SynchronizedDispatcher
    {
        private static readonly object queueLock = new object();

        private static readonly List<Task> taskQueue = new List<Task>();

        static SynchronizedDispatcher()
        {
            SynchronizedDispatcher.Thread = new Thread(Run)
            {
                IsBackground = true
            };

            SynchronizedDispatcher.Thread.Start();
        }

        private delegate void Action();

        private static bool Abort { get; set; }

        private static Thread Thread { get; set; }

        /// <summary>
        /// Invokes specified delegate with parameters on the dispatcher thread synchronously.
        /// </summary>
        /// <param name="task">delegate to run on the thread</param>
        /// <param name="args">arguments to supply to the delegate</param>
        /// <returns>result of an action</returns>
        public static TResult Invoke<TResult>(Func<TResult> @delegate)
        {
            // create the task
            var task = new Task<TResult>(@delegate);

            // we don't want the task to be completed before we start waiting for that, so the outer lock
            lock (task)
            {
                lock (queueLock)
                {
                    taskQueue.Add(task);

                    Monitor.Pulse(queueLock);
                }

                // until this point, evaluation could not start
                Monitor.Wait(task);

                if (task.Exception != null)
                {
                    throw task.Exception;
                }

                // and when we're done waiting, we know that the result was already set
                return task.Result;
            }
        }

        /// <summary>
        /// Tells the dispatcher to shutdown its worker thread.
        /// </summary>
        public static void Terminate()
        {
            SynchronizedDispatcher.Abort = true;
        }

        /// <summary>
        /// This method is used as a Thread.Run for the delegate hosting thread.
        /// </summary>
        private static void Run()
        {
            try
            {
                while (!SynchronizedDispatcher.Abort)
                {
                    Task task;

                    lock (queueLock)
                    {
                        if (taskQueue.Count > 0)
                        {
                            task = taskQueue[0];
                            taskQueue.RemoveAt(0);
                        }
                        else
                        {
                            Monitor.Wait(queueLock);
                            continue;
                        }
                    }

                    // if there's a task, process it asynchronously
                    lock (task)
                    {
                        try
                        {
                            task.Action.DynamicInvoke();
                        }
                        catch (TargetInvocationException e)
                        {
                            Tracer.Critical(string.Format("Exception in SynchronizedDispatcherThread \"{0}\"", Thread.CurrentThread.Name), e);
                            task.Exception = e.InnerException;
                        }

                        // notify waiting thread about completeion
                        Monitor.Pulse(task);
                    }
                }
            }
            catch (ThreadAbortException)
            {
            }
        }

        private class Task
        {
            public virtual Action Action { get; protected set; }

            public Exception Exception { get; set; }
        }

        /// <summary>
        /// Task object that's pushed to the queue.
        /// </summary>
        private class Task<TResult> : Task
        {
            public Task(Func<TResult> @delegate)
            {
                this.Delegate = @delegate;
                this.Action = () => this.Result = this.Delegate();
            }

            // task code
            public Func<TResult> Delegate { get; private set; }

            // result, filled out after it's executed
            public TResult Result { get; private set; }
        }
    }
}