namespace Unosquare.FFME
{
    using Primitives;
    using Shared;
    using System;
    using System.Runtime.CompilerServices;
    using Workers;

    public partial class MediaEngine
    {
        /// <summary>
        /// This partial class implements:
        /// 1. Packet reading from the Container
        /// 2. Frame Decoding from packet buffer and Block buffering
        /// 3. Block Rendering from block buffer
        /// </summary>

        #region State Management

        private readonly AtomicBoolean m_HasDecodingEnded = new AtomicBoolean(false);

        /// <summary>
        /// Holds the materialized block cache for each media type.
        /// </summary>
        public MediaTypeDictionary<MediaBlockBuffer> Blocks { get; } = new MediaTypeDictionary<MediaBlockBuffer>();

        /// <summary>
        /// Gets the preloaded subtitle blocks.
        /// </summary>
        public MediaBlockBuffer PreloadedSubtitles { get; private set; }

        /// <summary>
        /// Gets the reading worker.
        /// </summary>
        internal ReadingWorker ReadingWorker { get; private set; }

        /// <summary>
        /// Gets the decoding worker.
        /// </summary>
        internal DecodingWorker DecodingWorker { get; private set; }

        /// <summary>
        /// Gets the rendering worker.
        /// </summary>
        internal RenderingWorker RenderingWorker { get; private set; }

        /// <summary>
        /// Holds the block renderers
        /// TODO: Move this to the <see cref="RenderingWorker"/>
        /// </summary>
        internal MediaTypeDictionary<IMediaRenderer> Renderers { get; } = new MediaTypeDictionary<IMediaRenderer>();

        /// <summary>
        /// Gets or sets a value indicating whether the decoder worker has decoded all frames.
        /// This is an indication that the rendering worker should probe for end of media scenarios
        /// </summary>
        internal bool HasDecodingEnded
        {
            get => m_HasDecodingEnded.Value;
            set => m_HasDecodingEnded.Value = value;
        }

        /// <summary>
        /// Gets the buffer length maximum.
        /// port of MAX_QUEUE_SIZE (ffplay.c)
        /// </summary>
        internal long BufferLengthMax => 16 * 1024 * 1024;

        /// <summary>
        /// Gets a value indicating whether packets can be read and
        /// room is available in the download cache.
        /// </summary>
        internal bool ShouldReadMorePackets
        {
            get
            {
                if (Container?.Components == null)
                    return false;

                if (Container.IsReadAborted || Container.IsAtEndOfStream)
                    return false;

                // If it's a live stream always continue reading, regardless
                if (Container.IsLiveStream)
                    return true;

                // For network streams always expect a minimum buffer length
                if (Container.IsNetworkStream && Container.Components.BufferLength < BufferLengthMax)
                    return true;

                // if we don't have enough packets queued we should read
                return Container.Components.HasEnoughPackets == false;
            }
        }

        #endregion

        #region Methods

        /// <summary>
        /// Initializes the media block buffers and
        /// starts packet reader, frame decoder, and block rendering workers.
        /// </summary>
        internal void StartWorkers()
        {
            // Initialize the workers
            ReadingWorker = new ReadingWorker(this);
            DecodingWorker = new DecodingWorker(this);
            RenderingWorker = new RenderingWorker(this);

            // Initialize the block buffers
            // TODO initialize in rendering worker
            foreach (var t in Container.Components.MediaTypes)
                Blocks[t] = new MediaBlockBuffer(Constants.MaxBlocks[t], t);

            Clock.SpeedRatio = Constants.Controller.DefaultSpeedRatio;
            ReadingWorker.Start();
            DecodingWorker.Start();
            RenderingWorker.Start();
        }

        internal void SuspendWorkers()
        {
            ReadingWorker.Suspend();
            RenderingWorker.Suspend();
            DecodingWorker.Suspend();
        }

        internal void ResumeWorkers()
        {
            ReadingWorker.Resume();
            RenderingWorker.Resume();
            DecodingWorker.Resume();
        }

        /// <summary>
        /// Stops the packet reader, frame decoder, and block renderers
        /// </summary>
        internal void StopWorkers()
        {
            // Pause the clock so no further updates are propagated
            Clock.Pause();

            // Cause an immediate Packet read abort
            Container?.SignalAbortReads(false);

            // Stop the rendering worker before anything else
            RenderingWorker.Stop();
            DecodingWorker.Stop();
            ReadingWorker.Stop();

            // Call close on all renderers
            // TODO: Move to RenderingWorker Stop Logic
            foreach (var renderer in Renderers.Values)
                renderer.Close();

            // Set the threads to null
            RenderingWorker.Dispose();
            DecodingWorker.Dispose();
            ReadingWorker.Dispose();

            RenderingWorker = null;
            DecodingWorker = null;
            ReadingWorker = null;

            // Remove the renderers disposing of them
            Renderers.Clear();

            // Reset the clock
            ResetPosition();
        }

        /// <summary>
        /// Pre-loads the subtitles from the MediaOptions.SubtitlesUrl.
        /// </summary>
        internal void PreLoadSubtitles()
        {
            DisposePreloadedSubtitles();
            var subtitlesUrl = Container.MediaOptions.SubtitlesUrl;

            // Don't load a thing if we don't have to
            if (string.IsNullOrWhiteSpace(subtitlesUrl))
                return;

            try
            {
                PreloadedSubtitles = LoadBlocks(subtitlesUrl, MediaType.Subtitle, this);

                // Process and adjust subtitle delays if necessary
                if (Container.MediaOptions.SubtitlesDelay != TimeSpan.Zero)
                {
                    var delay = Container.MediaOptions.SubtitlesDelay;
                    for (var i = 0; i < PreloadedSubtitles.Count; i++)
                    {
                        var target = PreloadedSubtitles[i];
                        target.StartTime = TimeSpan.FromTicks(target.StartTime.Ticks + delay.Ticks);
                        target.EndTime = TimeSpan.FromTicks(target.EndTime.Ticks + delay.Ticks);
                        target.Duration = TimeSpan.FromTicks(target.EndTime.Ticks - target.StartTime.Ticks);
                    }
                }

                Container.MediaOptions.IsSubtitleDisabled = true;
            }
            catch (MediaContainerException mex)
            {
                DisposePreloadedSubtitles();
                this.LogWarning(Aspects.Component,
                    $"No subtitles to side-load found in media '{subtitlesUrl}'. {mex.Message}");
            }
        }

        /// <summary>
        /// Returns the value of a discrete frame position of the main media component if possible.
        /// Otherwise, it simply rounds the position to the nearest millisecond.
        /// </summary>
        /// <param name="position">The position.</param>
        /// <returns>The snapped, discrete, normalized position</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal TimeSpan SnapPositionToBlockPosition(TimeSpan position)
        {
            if (Container == null)
                return position.Normalize();

            var blocks = Blocks.Main(Container);
            if (blocks == null) return position.Normalize();

            return blocks.GetSnapPosition(position) ?? position.Normalize();
        }

        /// <summary>
        /// Resumes the playback by resuming the clock and updating the playback state to state.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void ResumePlayback()
        {
            Clock.Play();
            State.UpdateMediaState(PlaybackStatus.Play);
        }

        /// <summary>
        /// Updates the clock position and notifies the new
        /// position to the <see cref="State" />.
        /// </summary>
        /// <param name="position">The position.</param>
        /// <returns>The newly set position</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal TimeSpan ChangePosition(TimeSpan position)
        {
            Clock.Update(position);
            State.UpdatePosition();
            return position;
        }

        /// <summary>
        /// Resets the clock to the zero position and notifies the new
        /// position to rhe <see cref="State"/>.
        /// </summary>
        /// <returns>The newly set position</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal TimeSpan ResetPosition()
        {
            Clock.Reset();
            State.UpdatePosition();
            return TimeSpan.Zero;
        }

        /// <summary>
        /// Gets a value indicating whether more frames can be decoded into blocks of the given type.
        /// </summary>
        /// <param name="t">The media type.</param>
        /// <returns>
        ///   <c>true</c> if more frames can be decoded; otherwise, <c>false</c>.
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool CanReadMoreFramesOf(MediaType t)
        {
            return
                Container.Components[t].BufferLength > 0 ||
                Container.Components[t].HasPacketsInCodec ||
                ShouldReadMorePackets;
        }

        #endregion
    }
}
