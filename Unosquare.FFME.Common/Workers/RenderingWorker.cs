namespace Unosquare.FFME.Workers
{
    using Primitives;
    using Shared;
    using System;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Threading;

    internal sealed class RenderingWorker : MediaWorker
    {
        private MediaType main;
        private MediaType[] all;
        private TimeSpan wallClock;

        public RenderingWorker(MediaEngine mediaCore)
            : base(nameof(RenderingWorker), ThreadPriority.Lowest, TimeSpan.FromMilliseconds(30), mediaCore)
        {
            IsInDebugMode = MediaEngine.Platform?.IsInDebugMode ?? false;
            InitializeRenderers();
        }

        /// <summary>
        /// Holds the last rendered StartTime for each of the media block types
        /// </summary>
        public MediaTypeDictionary<TimeSpan> LastRenderTime { get; } = new MediaTypeDictionary<TimeSpan>();

        public MediaTypeDictionary<MediaBlock> CurrentBlock { get; } = new MediaTypeDictionary<MediaBlock>();

        private MediaBlockBuffer PreloadedSubtitles => MediaCore.PreloadedSubtitles;

        private bool IsInDebugMode { get; }

        /// <summary>
        /// Invalidates the last render time for the given component.
        /// Additionally, it calls Seek on the renderer to remove any caches
        /// </summary>
        /// <param name="t">The t.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void InvalidateRenderer(MediaType t)
        {
            // This forces the rendering worker to send the
            // corresponding block to its renderer
            LastRenderTime[t] = TimeSpan.MinValue;
            Renderers[t]?.Seek();
        }

        public void InitializeRenderers()
        {
            main = Components.MainMediaType;
            all = Components.MediaTypes.ToArray();

            foreach (var t in all)
                Renderers[t] = MediaEngine.Platform.CreateRenderer(t, MediaCore);

            if (PreloadedSubtitles != null)
            {
                var t = PreloadedSubtitles.MediaType;
                Renderers[t] = MediaEngine.Platform.CreateRenderer(t, MediaCore);
            }

            foreach (var t in all)
                InvalidateRenderer(t);
        }

        protected override void OnWorkerStarted()
        {
            main = Components.MainMediaType;
            all = Renderers.Keys.ToArray();

            // wait for main component blocks or EOF or cancellation pending
            while (MediaCore.CanReadMoreFramesOf(main) && Blocks[main].Count <= 0)
                MediaCore.FrameDecodingCycle.Wait(Constants.Interval.LowPriority);

            // Set the initial clock position
            wallClock = MediaCore.ChangePosition(Blocks[main].RangeStartTime);

            // Wait for renderers to be ready
            foreach (var t in all)
                Renderers[t]?.WaitForReadyState();
        }

        protected override void ExecuteWorkerCycle()
        {
            // Prevent rendering if we are busy
            if (Commands.IsExecutingDirectCommand)
                return;

            Commands.WaitForActiveSeekCommand();

            // Update Status Properties
            main = Components.MainMediaType;
            all = Renderers.Keys.ToArray();
            wallClock = MediaCore.WallClock;

            // Capture the blocks to render
            foreach (var t in all)
            {
                // Get the audio, video, or subtitle block to render
                CurrentBlock[t] = t == MediaType.Subtitle && PreloadedSubtitles != null ?
                    PreloadedSubtitles[wallClock] :
                    Blocks[t][wallClock];
            }

            // Render each of the Media Types if it is time to do so.
            foreach (var t in all)
            {
                // Skip rendering for nulls
                if (CurrentBlock[t] == null || CurrentBlock[t].IsDisposed)
                    continue;

                // Render by forced signal (TimeSpan.MinValue) or because simply it is time to do so
                if (LastRenderTime[t] == TimeSpan.MinValue || CurrentBlock[t].StartTime != LastRenderTime[t])
                    SendBlockToRenderer(CurrentBlock[t], wallClock);
            }

            // Call the update method on all renderers so they receive what the new wall clock is.
            foreach (var t in all)
                Renderers[t]?.Update(wallClock);

            // Check End of Media Scenarios
            if (MediaCore.HasDecodingEnded
            && Commands.IsSeeking == false
            && MediaCore.WallClock >= LastRenderTime[main]
            && MediaCore.WallClock >= Blocks[main].RangeEndTime)
            {
                // Rendered all and nothing else to render
                if (State.HasMediaEnded == false)
                {
                    MediaCore.Clock.Pause();
                    var endPosition = MediaCore.ChangePosition(Blocks[main].RangeEndTime);
                    State.UpdateMediaEnded(true, endPosition);
                    State.UpdateMediaState(PlaybackStatus.Stop);
                    foreach (var mt in Container.Components.MediaTypes)
                        InvalidateRenderer(mt);

                    MediaCore.SendOnMediaEnded();
                }
            }
            else
            {
                State.UpdateMediaEnded(false, TimeSpan.Zero);
            }

            // Update the Position
            if ((IsWorkerInterruptRequested || MediaCore.IsSyncBuffering) == false)
                State.UpdatePosition();
        }

        protected override void OnWorkerStopped()
        {
            base.OnWorkerStopped();
        }

        protected override void OnWorkerDisposing()
        {
            base.OnWorkerDisposing();
        }

        /// <summary>
        /// Sends the given block to its corresponding media renderer.
        /// </summary>
        /// <param name="block">The block.</param>
        /// <param name="clockPosition">The clock position.</param>
        /// <returns>
        /// The number of blocks sent to the renderer
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int SendBlockToRenderer(MediaBlock block, TimeSpan clockPosition)
        {
            // No blocks were rendered
            if (block == null) return 0;

            // Process property changes coming from video blocks
            State.UpdateDynamicBlockProperties(block, Blocks[block.MediaType]);

            // Send the block to its corresponding renderer
            Renderers[block.MediaType]?.Render(block, clockPosition);
            LastRenderTime[block.MediaType] = block.StartTime;

            // Log the block statistics for debugging
            LogRenderBlock(block, clockPosition, block.Index);

            // At this point, we are certain that a blocl has been
            // sent to its corresponding renderer.
            return 1;
        }

        /// <summary>
        /// Logs a block rendering operation as a Trace Message
        /// if the debugger is attached.
        /// </summary>
        /// <param name="block">The block.</param>
        /// <param name="clockPosition">The clock position.</param>
        /// <param name="renderIndex">Index of the render.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void LogRenderBlock(MediaBlock block, TimeSpan clockPosition, int renderIndex)
        {
            // Prevent logging for production use
            if (IsInDebugMode == false) return;

            try
            {
                var drift = TimeSpan.FromTicks(clockPosition.Ticks - block.StartTime.Ticks);
                MediaCore.LogTrace(Aspects.RenderingWorker,
                    $"{block.MediaType.ToString().Substring(0, 1)} "
                    + $"BLK: {block.StartTime.Format()} | "
                    + $"CLK: {clockPosition.Format()} | "
                    + $"DFT: {drift.TotalMilliseconds,4:0} | "
                    + $"IX: {renderIndex,3} | "
                    + $"PQ: {Container?.Components[block.MediaType]?.BufferLength / 1024d,7:0.0}k | "
                    + $"TQ: {Container?.Components.BufferLength / 1024d,7:0.0}k");
            }
            catch
            {
                // swallow
            }
        }
    }
}
