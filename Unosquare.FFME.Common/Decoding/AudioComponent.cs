namespace Unosquare.FFME.Decoding
{
    using Core;
    using FFmpeg.AutoGen;
    using Shared;
    using System;
    using System.Linq;

    /// <summary>
    /// Provides audio sample extraction, decoding and scaling functionality.
    /// </summary>
    /// <seealso cref="MediaComponent" />
    internal sealed unsafe class AudioComponent : MediaComponent
    {
        #region Private Declarations

        private readonly string FilterString;

        /// <summary>
        /// Holds a reference to the audio re-sampler
        /// This re-sampler gets disposed upon disposal of this object.
        /// </summary>
        private SwrContext* Scaler;

        /// <summary>
        /// Used to determine if we have to reset the scaler parameters
        /// </summary>
        private FFAudioParams LastSourceSpec;

        private AVFilterGraph* FilterGraph;
        private AVFilterContext*[] SourceFilter;
        private AVFilterContext* SinkFilter;
        private AVFilterInOut* SinkInput;
        private AVFilterInOut* SourceOutput;

        private string[] CurrentFilterArguments;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the <see cref="AudioComponent"/> class.
        /// </summary>
        /// <param name="container">The container.</param>
        /// <param name="streamIndexes">Index of the stream.</param>
        internal AudioComponent(MediaContainer container, params int[] streamIndexes)
            : base(container, streamIndexes)
        {
            // TODO: Fix a constant
            Channels = Stream(0)->codec->channels;
            SampleRate = Stream(0)->codec->sample_rate;
            BitsPerSample = ffmpeg.av_samples_get_buffer_size(null, 1, 1, Stream(0)->codec->sample_fmt, 1) * 8;
            FilterString = container.MediaOptions.AudioFilter;
            CurrentFilterArguments = new string[0];
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the number of audio channels.
        /// </summary>
        public int Channels { get; }

        /// <summary>
        /// Gets the audio sample rate.
        /// </summary>
        public int SampleRate { get; }

        /// <summary>
        /// Gets the bits per sample.
        /// </summary>
        public int BitsPerSample { get; }

        #endregion

        #region Methods

        /// <inheritdoc />
        public override bool MaterializeFrame(MediaFrame input, ref MediaBlock output, MediaBlock previousBlock)
        {
            if (output == null) output = new AudioBlock();
            if (input is AudioFrame == false || output is AudioBlock == false)
                throw new ArgumentNullException($"{nameof(input)} and {nameof(output)} are either null or not of a compatible media type '{MediaType}'");

            var source = (AudioFrame)input;
            var target = (AudioBlock)output;

            // Create the source and target audio specs. We might need to scale from
            // the source to the target
            var sourceSpec = FFAudioParams.CreateSource(source.Pointer);
            var targetSpec = FFAudioParams.CreateTarget(source.Pointer);

            // Initialize or update the audio scaler if required
            if (Scaler == null || LastSourceSpec == null || FFAudioParams.AreCompatible(LastSourceSpec, sourceSpec) == false)
            {
                Scaler = ffmpeg.swr_alloc_set_opts(
                    Scaler,
                    targetSpec.ChannelLayout,
                    targetSpec.Format,
                    targetSpec.SampleRate,
                    sourceSpec.ChannelLayout,
                    sourceSpec.Format,
                    sourceSpec.SampleRate,
                    0,
                    null);

                RC.Current.Add(Scaler);
                ffmpeg.swr_init(Scaler);
                LastSourceSpec = sourceSpec;
            }

            // Allocate the unmanaged output buffer and convert to stereo.
            int outputSamplesPerChannel;
            if (target.Allocate(targetSpec.BufferLength) &&
                target.TryAcquireWriterLock(out var writeLock))
            {
                using (writeLock)
                {
                    var outputBufferPtr = (byte*)target.Buffer;

                    // Execute the conversion (audio scaling). It will return the number of samples that were output
                    outputSamplesPerChannel = ffmpeg.swr_convert(
                        Scaler,
                        &outputBufferPtr,
                        targetSpec.SamplesPerChannel,
                        source.Pointer->extended_data,
                        source.Pointer->nb_samples);
                }
            }
            else
            {
                return false;
            }

            // Compute the buffer length
            var outputBufferLength =
                ffmpeg.av_samples_get_buffer_size(null, targetSpec.ChannelCount, outputSamplesPerChannel, targetSpec.Format, 1);

            // Flag the block if we have to
            target.IsStartTimeGuessed = source.HasValidStartTime == false;

            // Try to fix the start time, duration and End time if we don't have valid data
            if (source.HasValidStartTime == false && previousBlock != null)
            {
                // Get timing information from the previous block
                target.StartTime = TimeSpan.FromTicks(previousBlock.EndTime.Ticks + 1);
                target.Duration = source.Duration.Ticks > 0 ? source.Duration : previousBlock.Duration;
                target.EndTime = TimeSpan.FromTicks(target.StartTime.Ticks + target.Duration.Ticks);
            }
            else
            {
                // We set the target properties directly from the source
                target.StartTime = source.StartTime;
                target.Duration = source.Duration;
                target.EndTime = source.EndTime;
            }

            target.CompressedSize = source.CompressedSize;
            target.SamplesBufferLength = outputBufferLength;
            target.ChannelCount = targetSpec.ChannelCount;

            target.SampleRate = targetSpec.SampleRate;
            target.SamplesPerChannel = outputSamplesPerChannel;
            target.StreamIndex = input.StreamIndex;

            return true;
        }

        /// <inheritdoc />
        protected override MediaFrame CreateFrameSource(params IntPtr[] framePointers)
        {
            // Validate the audio frames
            {
                bool toReturn = false;
                for (int i = 0; i < framePointers.Length; i++)
                {
                    var frame = (AVFrame*)framePointers[i];
                    toReturn |= framePointers[i] == IntPtr.Zero || frame->channels <= 0 || frame->nb_samples <= 0 || frame->sample_rate <= 0;
                }

                if (toReturn) return null;
            }

            AVFrame*[] frames = new AVFrame*[framePointers.Length];
            for (int i = 0; i < framePointers.Length; i++)
            {
                frames[i] = (AVFrame*)framePointers[i];
            }

            if (string.IsNullOrWhiteSpace(FilterString) == false)
                InitializeFilterGraph(frames);

            AVFrame* outputFrame;

            // Filter Graph can be changed by issuing a ChangeMedia command
            if (FilterGraph != null)
            {
                // Allocate the output frame
                outputFrame = MediaFrame.CloneAVFrame(frames[0]);

                int result = 0;
                for (int i = 0; i < framePointers.Length; i++)
                {
                    result = ffmpeg.av_buffersrc_add_frame(SourceFilter[i], frames[i]);
                    if (result < 0) break;
                }

                while (result >= 0)
                    result = ffmpeg.av_buffersink_get_frame_flags(SinkFilter, outputFrame, 0);

                if (outputFrame->nb_samples <= 0)
                {
                    // If we don't have a valid output frame simply release it and
                    // return the original input frame
                    MediaFrame.ReleaseAVFrame(outputFrame);
                    outputFrame = frames[0];
                }
                else
                {
                    // the output frame is the new valid frame (output frame).
                    // theretofore, we need to release the original
                    for (int i = 0; i < frames.Length; i++)
                        MediaFrame.ReleaseAVFrame(frames[i]);
                }
            }
            else
            {
                outputFrame = frames[0];
            }

            // Check if the output frame is valid
            if (outputFrame->nb_samples <= 0)
                return null;

            var frameHolder = new AudioFrame(outputFrame, this);
            return frameHolder;
        }

        #endregion

        #region IDisposable Support

        /// <inheritdoc />
        protected override void Dispose(bool alsoManaged)
        {
            RC.Current.Remove(Scaler);
            if (Scaler != null)
            {
                var scalerRef = Scaler;
                ffmpeg.swr_free(&scalerRef);
                Scaler = null;
            }

            DestroyFilterGraph();
            base.Dispose(alsoManaged);
        }

        #endregion

        #region Filtering Methods

        /// <summary>
        /// Destroys the filter graph releasing unmanaged resources.
        /// </summary>
        private void DestroyFilterGraph()
        {
            if (FilterGraph == null) return;
            RC.Current.Remove(FilterGraph);
            var filterGraphRef = FilterGraph;
            ffmpeg.avfilter_graph_free(&filterGraphRef);

            FilterGraph = null;
            SinkInput = null;
            SourceOutput = null;
        }

        /// <summary>
        /// Computes the frame filter arguments that are appropriate for the audio filtering chain.
        /// </summary>
        /// <param name="frame">The frame.</param>
        /// <returns>The base filter arguments</returns>
        private string ComputeFilterArguments(AVFrame* frame)
        {
            var hexChannelLayout = BitConverter.ToString(
                BitConverter.GetBytes(frame->channel_layout).Reverse().ToArray()).Replace("-", string.Empty);

            var channelLayout = $"0x{hexChannelLayout.ToLowerInvariant()}";

            var arguments =
                 $"time_base={Stream(0)->time_base.num}/{Stream(0)->time_base.den}:" +
                 $"sample_rate={frame->sample_rate:0}:" +
                 $"sample_fmt={ffmpeg.av_get_sample_fmt_name((AVSampleFormat)frame->format)}:" +
                 $"channel_layout={channelLayout}";

            return arguments;
        }

        /// <summary>
        /// If necessary, disposes the existing filter graph and creates a new one based on the frame arguments.
        /// </summary>
        /// <param name="frames">The frame.</param>
        /// <exception cref="MediaContainerException">
        /// avfilter_graph_create_filter
        /// or
        /// avfilter_graph_create_filter
        /// or
        /// avfilter_link
        /// or
        /// avfilter_graph_parse
        /// or
        /// avfilter_graph_config
        /// </exception>
        private void InitializeFilterGraph(AVFrame*[] frames)
        {
            /*
             * References:
             * https://www.ffmpeg.org/doxygen/2.0/doc_2examples_2filtering_audio_8c-example.html
             */

            // ReSharper disable StringLiteralTypo
            const string SourceFilterName = "abuffer";
            const string SourceFilterInstance = "audio_buffer";
            const string SinkFilterName = "abuffersink";
            const string SinkFilterInstance = "audio_buffersink";

            // ReSharper restore StringLiteralTypo
            string[] frameArgumentsArray = new string[StreamIndexes.Length];
            for (int i = 0; i < StreamIndexes.Length; i++)
            {
                frameArgumentsArray[i] = ComputeFilterArguments(frames[i]);
            }

            if (frameArgumentsArray.Length != CurrentFilterArguments.Length)
            {
                DestroyFilterGraph();
            }
            else
            {
                bool notEqual = false;
                for (int i = 0; i < CurrentFilterArguments.Length; i++)
                {
                    notEqual |= string.IsNullOrEmpty(CurrentFilterArguments[i]) || !frameArgumentsArray[i].Equals(CurrentFilterArguments[i]);
                }

                if (notEqual)
                    DestroyFilterGraph();
                else
                    return;
            }

            FilterGraph = ffmpeg.avfilter_graph_alloc();
            RC.Current.Add(FilterGraph);
            CurrentFilterArguments = frameArgumentsArray;

            try
            {
                int result;
                AVFilterContext* sinkFilterRef = null;
                result = ffmpeg.avfilter_graph_create_filter(
                        &sinkFilterRef, ffmpeg.avfilter_get_by_name(SinkFilterName), SinkFilterInstance, null, null, FilterGraph);
                SinkFilter = sinkFilterRef;
                if (result != 0)
                {
                    throw new MediaContainerException(
                        $"{nameof(ffmpeg.avfilter_graph_create_filter)} ({SinkFilterInstance}) failed. Error {result}: {FFInterop.DecodeMessage(result)}");
                }

                SinkInput = ffmpeg.avfilter_inout_alloc();
                SinkInput->name = ffmpeg.av_strdup("out");
                SinkInput->filter_ctx = SinkFilter;
                SinkInput->pad_idx = 0;
                SinkInput->next = null;

                var initFilterCount = FilterGraph->nb_filters;

                SourceFilter = new AVFilterContext*[StreamIndexes.Length];
                for (int i = 0; i < StreamIndexes.Length; i++)
                {
                    AVFilterContext* sourceFilterRef = null;

                    result = ffmpeg.avfilter_graph_create_filter(
                        &sourceFilterRef, ffmpeg.avfilter_get_by_name(SourceFilterName), SourceFilterInstance, CurrentFilterArguments[i], null, FilterGraph);
                    if (result != 0)
                    {
                        throw new MediaContainerException(
                            $"{nameof(ffmpeg.avfilter_graph_create_filter)} ({SourceFilterInstance}) failed. Error {result}: {FFInterop.DecodeMessage(result)}");
                    }

                    SourceFilter[i] = sourceFilterRef;

                    if (string.IsNullOrWhiteSpace(FilterString))
                    {
                        result = ffmpeg.avfilter_link(SourceFilter[i], 0, SinkFilter, 0);
                        if (result != 0)
                            throw new MediaContainerException($"{nameof(ffmpeg.avfilter_link)} failed. Error {result}: {FFInterop.DecodeMessage(result)}");
                    }
                    else
                    {
                        AVFilterInOut* sourceOutput = ffmpeg.avfilter_inout_alloc();
                        sourceOutput->name = ffmpeg.av_strdup($"in{i}");
                        sourceOutput->filter_ctx = SourceFilter[i];
                        sourceOutput->pad_idx = 0;
                        sourceOutput->next = null;

                        if (i == 0)
                        {
                            SourceOutput = sourceOutput;
                        }
                        else
                        {
                            AVFilterInOut* node = SourceOutput;
                            for (int j = 1; j < i; j++)
                                node = node->next;
                            node->next = sourceOutput;
                        }
                    }
                }

                result = ffmpeg.avfilter_graph_parse(FilterGraph, FilterString, SinkInput, SourceOutput, null);
                if (result != 0)
                    throw new MediaContainerException($"{nameof(ffmpeg.avfilter_graph_parse)} failed. Error {result}: {FFInterop.DecodeMessage(result)}");

                // Reorder the filters to ensure that inputs of the custom filters are merged first
                for (var i = 0; i < FilterGraph->nb_filters - initFilterCount; i++)
                {
                    var sourceAddress = FilterGraph->filters[i];
                    var targetAddress = FilterGraph->filters[i + initFilterCount];
                    FilterGraph->filters[i] = targetAddress;
                    FilterGraph->filters[i + initFilterCount] = sourceAddress;
                }

                result = ffmpeg.avfilter_graph_config(FilterGraph, null);
                if (result != 0)
                    throw new MediaContainerException($"{nameof(ffmpeg.avfilter_graph_config)} failed. Error {result}: {FFInterop.DecodeMessage(result)}");
            }
            catch (Exception ex)
            {
                this.LogError(Aspects.Component, $"Audio filter graph could not be built: {FilterString}.", ex);
                DestroyFilterGraph();
            }
        }

        #endregion
    }
}
