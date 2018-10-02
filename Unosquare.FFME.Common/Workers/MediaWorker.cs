namespace Unosquare.FFME.Workers
{
    using Commands;
    using Decoding;
    using Primitives;
    using Shared;
    using System;
    using System.Threading;

    internal abstract class MediaWorker : BackgroundWorkerBase
    {
        protected MediaWorker(string workerName, ThreadPriority priority, TimeSpan cyclePeriod, MediaEngine mediaCore)
            : base(workerName, priority, cyclePeriod)
        {
            MediaCore = mediaCore;
            Commands = mediaCore.Commands;
            State = mediaCore.State;
            Container = mediaCore.Container;
            Components = mediaCore.Container.Components;
            Renderers = mediaCore.Renderers;
            Blocks = mediaCore.Blocks;
            IsInDebugMode = MediaEngine.Platform?.IsInDebugMode ?? false;
        }

        protected MediaEngine MediaCore { get; }

        protected CommandManager Commands { get; }

        protected MediaEngineState State { get; }

        protected MediaContainer Container { get; }

        protected MediaComponentSet Components { get; }

        protected MediaTypeDictionary<IMediaRenderer> Renderers { get; }

        protected MediaTypeDictionary<MediaBlockBuffer> Blocks { get; }

        protected bool IsInDebugMode { get; }

        /// <summary>
        /// Gets a value indicating whether a worker interrupt has been requested by the command manager.
        /// This instructs potentially long loops in workers to immediately exit.
        /// </summary>
        protected bool IsWorkerInterruptRequested =>
            Commands.IsSeeking ||
            Commands.IsChanging ||
            Commands.IsClosing ||
            Commands.IsStopWorkersPending;
    }
}
