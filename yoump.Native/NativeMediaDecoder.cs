using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;

namespace yoump.Native
{
    public unsafe class NativeMediaDecoder : IDisposable
    {
        static NativeMediaDecoder()
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string ffmpegBinPath = Path.Combine(appDir, "ffmpeg", "bin");
            ffmpeg.RootPath = ffmpegBinPath;
        }

        private AVFormatContext* _formatContext;
        private AVCodecContext* _codecContext;
        private SwsContext* _swsContext;

        private AVBufferRef* _hwDeviceCtx = null;
        private AVBufferRef* _hwFramesCtx = null;

        private readonly int _videoStreamIndex = -1;
        private AVFrame* _frame;
        private AVFrame* _swFrame;
        private AVPacket* _packet;

        private readonly double _timeBase;
        private bool _disposed = false;
        private bool _hasBufferedFrame = false;
        private bool _isEndOfFile = false;

        public int VideoWidth => _codecContext != null ? _codecContext->width : 0;
        public int VideoHeight => _codecContext != null ? _codecContext->height : 0;

        public double Framerate
        {
            get
            {
                if (_formatContext == null || _videoStreamIndex < 0) return 30.0;
                var stream = _formatContext->streams[_videoStreamIndex];
                double fps = stream->avg_frame_rate.num / (double)Math.Max(1, stream->avg_frame_rate.den);
                if (fps <= 0.0 || double.IsNaN(fps))
                    fps = stream->r_frame_rate.num / (double)Math.Max(1, stream->r_frame_rate.den);
                if (fps <= 0.0 || double.IsNaN(fps)) fps = 30.0;
                return fps;
            }
        }

        public NativeMediaDecoder(string filePath) : this(filePath, IntPtr.Zero) { }

        public NativeMediaDecoder(string filePath, IntPtr d3d11DevicePtr)
        {
            ffmpeg.avformat_network_init();

            try
            {
                AVFormatContext* pFormatContext = ffmpeg.avformat_alloc_context();
                if (ffmpeg.avformat_open_input(&pFormatContext, filePath, null, null) < 0)
                    throw new Exception($"Не удалось открыть файл: {filePath}");
                _formatContext = pFormatContext;

                if (ffmpeg.avformat_find_stream_info(_formatContext, null) < 0)
                    throw new Exception("Не удалось получить информацию о потоках");

                AVCodec* pCodec = null;
                _videoStreamIndex = ffmpeg.av_find_best_stream(_formatContext, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &pCodec, 0);

                if (_videoStreamIndex < 0) return;

                var stream = _formatContext->streams[_videoStreamIndex];
                _timeBase = stream->time_base.num / (double)stream->time_base.den;

                _codecContext = ffmpeg.avcodec_alloc_context3(pCodec);

                // Аппаратное ускорение всегда активно (Zero-Copy)
                if (d3d11DevicePtr != IntPtr.Zero)
                {
                    _hwDeviceCtx = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
                    if (_hwDeviceCtx != null)
                    {
                        AVHWDeviceContext* hwDeviceCtx = (AVHWDeviceContext*)_hwDeviceCtx->data;
                        AVD3D11VADeviceContext* d3d11Ctx = (AVD3D11VADeviceContext*)hwDeviceCtx->hwctx;

                        // ИСПРАВЛЕНИЕ УТЕЧКИ / КРАША: Обязательно увеличиваем счетчик ссылок COM-объекта
                        Marshal.AddRef(d3d11DevicePtr);
                        d3d11Ctx->device = (ID3D11Device*)d3d11DevicePtr.ToPointer();

                        if (ffmpeg.av_hwdevice_ctx_init(_hwDeviceCtx) >= 0)
                        {
                            _hwFramesCtx = ffmpeg.av_hwframe_ctx_alloc(_hwDeviceCtx);
                            if (_hwFramesCtx != null)
                            {
                                AVHWFramesContext* framesCtx = (AVHWFramesContext*)_hwFramesCtx->data;
                                framesCtx->format = AVPixelFormat.AV_PIX_FMT_D3D11;
                                framesCtx->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
                                framesCtx->width = stream->codecpar->width;
                                framesCtx->height = stream->codecpar->height;
                                framesCtx->initial_pool_size = 30;

                                if (ffmpeg.av_hwframe_ctx_init(_hwFramesCtx) >= 0)
                                {
                                    _codecContext->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDeviceCtx);
                                    _codecContext->hw_frames_ctx = ffmpeg.av_buffer_ref(_hwFramesCtx);
                                }
                                else
                                {
                                    fixed (AVBufferRef** p = &_hwFramesCtx) { ffmpeg.av_buffer_unref(p); }
                                    _hwFramesCtx = null;

                                    fixed (AVBufferRef** p = &_hwDeviceCtx) { ffmpeg.av_buffer_unref(p); }
                                    _hwDeviceCtx = null;
                                }
                            }
                        }
                        else
                        {
                            fixed (AVBufferRef** p = &_hwDeviceCtx) { ffmpeg.av_buffer_unref(p); }
                            _hwDeviceCtx = null;
                        }
                    }
                }
                else
                {
                    AVBufferRef* hwDeviceCtx = null;
                    if (ffmpeg.av_hwdevice_ctx_create(&hwDeviceCtx, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0) >= 0)
                    {
                        _hwDeviceCtx = hwDeviceCtx;
                        _codecContext->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDeviceCtx);
                    }
                }

                ffmpeg.avcodec_parameters_to_context(_codecContext, stream->codecpar);

                if (ffmpeg.avcodec_open2(_codecContext, pCodec, null) < 0)
                    throw new Exception("Не удалось открыть кодек");

                _frame = ffmpeg.av_frame_alloc();
                _swFrame = ffmpeg.av_frame_alloc();
                _packet = ffmpeg.av_packet_alloc();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        ~NativeMediaDecoder()
        {
            Dispose(false);
        }

        private double GetPtsMs(AVFrame* frame)
        {
            long pts = frame->best_effort_timestamp;
            if (pts == ffmpeg.AV_NOPTS_VALUE) pts = frame->pts;
            if (pts == ffmpeg.AV_NOPTS_VALUE) pts = 0;

            AVStream* stream = _formatContext->streams[_videoStreamIndex];
            long startPts = stream->start_time;

            if (startPts != ffmpeg.AV_NOPTS_VALUE)
            {
                pts -= startPts;
            }

            // ========================================================================
            // ИСПРАВЛЕНИЕ: Использование av_rescale_q для точного пересчета PTS в мс
            // Устраняет плавающий рассинхрон аудио/видео при сложных TimeBase
            // ========================================================================
            AVRational targetTimeBase = new() { num = 1, den = 1000 };
            long ptsMs = ffmpeg.av_rescale_q(pts, stream->time_base, targetTimeBase);

            return Math.Max(0.0, (double)ptsMs);
        }

        public bool TryReadNextHardwareFrame(out IntPtr pTexture2D, out int subresourceIndex, out double ptsMs)
        {
            pTexture2D = IntPtr.Zero;
            subresourceIndex = 0;
            ptsMs = 0;
            if (_disposed || _videoStreamIndex < 0) return false;

            if (_hasBufferedFrame)
            {
                if (_frame->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11)
                {
                    pTexture2D = (IntPtr)_frame->data[0];
                    subresourceIndex = (int)_frame->data[1];
                    ptsMs = GetPtsMs(_frame);
                    _hasBufferedFrame = false;
                    return true;
                }
                _hasBufferedFrame = false;
            }

            while (ffmpeg.av_read_frame(_formatContext, _packet) >= 0)
            {
                if (_packet->stream_index == _videoStreamIndex)
                {
                    int sendRet = ffmpeg.avcodec_send_packet(_codecContext, _packet);
                    while (sendRet >= 0 || sendRet == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    {
                        ffmpeg.av_frame_unref(_frame);

                        int recvRet = ffmpeg.avcodec_receive_frame(_codecContext, _frame);

                        if (recvRet == ffmpeg.AVERROR_EOF)
                        {
                            _isEndOfFile = true;
                            break;
                        }
                        if (recvRet == ffmpeg.AVERROR(ffmpeg.EAGAIN)) break;

                        if (recvRet >= 0)
                        {
                            ptsMs = GetPtsMs(_frame);
                            ffmpeg.av_packet_unref(_packet);

                            if (_frame->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11)
                            {
                                pTexture2D = (IntPtr)_frame->data[0];
                                subresourceIndex = (int)_frame->data[1];
                                return true;
                            }
                            ffmpeg.av_frame_unref(_frame);
                            return false;
                        }
                        if (sendRet == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                            sendRet = ffmpeg.avcodec_send_packet(_codecContext, _packet);
                    }
                }
                ffmpeg.av_packet_unref(_packet);
            }

            _isEndOfFile = true;

            ffmpeg.avcodec_send_packet(_codecContext, null);
            ffmpeg.av_frame_unref(_frame);

            int finalRecv = ffmpeg.avcodec_receive_frame(_codecContext, _frame);
            if (finalRecv == ffmpeg.AVERROR_EOF) _isEndOfFile = true;

            if (finalRecv >= 0)
            {
                if (_frame->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11)
                {
                    pTexture2D = (IntPtr)_frame->data[0];
                    subresourceIndex = (int)_frame->data[1];
                    ptsMs = GetPtsMs(_frame);
                    return true;
                }
                ffmpeg.av_frame_unref(_frame);
            }

            return false;
        }

        public bool ExtractFrameToBuffer(TimeSpan timePosition, IntPtr targetBufferPtr, int targetWidth, int targetHeight, int targetPitch, bool fastSeek = false)
        {
            if (_disposed || targetBufferPtr == IntPtr.Zero || _videoStreamIndex < 0) return false;

            Seek(timePosition, fastSeek);

            if (_hasBufferedFrame)
            {
                ProcessAndScaleFrame(targetBufferPtr, targetWidth, targetHeight, targetPitch);
                _hasBufferedFrame = false;
                return true;
            }

            return TryReadNextFrame(targetBufferPtr, targetWidth, targetHeight, targetPitch, out _);
        }

        public void Seek(TimeSpan timePosition, bool fastSeek = false)
        {
            if (_disposed || _videoStreamIndex < 0) return;

            AVStream* stream = _formatContext->streams[_videoStreamIndex];
            long startPts = stream->start_time;
            if (startPts == ffmpeg.AV_NOPTS_VALUE) startPts = 0;

            // ИСПРАВЛЕНИЕ: Точный расчет целевого PTS для Seek
            AVRational targetTimeBase = new() { num = 1, den = 1000 };
            long offsetPts = ffmpeg.av_rescale_q((long)timePosition.TotalMilliseconds, targetTimeBase, stream->time_base);
            long targetPts = startPts + offsetPts;

            long currentPts = _frame != null ? _frame->best_effort_timestamp : ffmpeg.AV_NOPTS_VALUE;
            if (currentPts == ffmpeg.AV_NOPTS_VALUE && _frame != null) currentPts = _frame->pts;
            if (currentPts == ffmpeg.AV_NOPTS_VALUE) currentPts = 0;

            double diffSec = (targetPts - currentPts) * _timeBase;

            if (fastSeek || diffSec < -0.05 || diffSec > 0.5 || currentPts == 0 || _isEndOfFile)
            {
                ffmpeg.av_frame_unref(_frame);
                _hasBufferedFrame = false;

                ffmpeg.av_seek_frame(_formatContext, _videoStreamIndex, targetPts, ffmpeg.AVSEEK_FLAG_BACKWARD);
                ffmpeg.avcodec_flush_buffers(_codecContext);
                _isEndOfFile = false;
            }

            _hasBufferedFrame = false;

            while (ffmpeg.av_read_frame(_formatContext, _packet) >= 0)
            {
                if (_packet->stream_index == _videoStreamIndex)
                {
                    int sendRet = ffmpeg.avcodec_send_packet(_codecContext, _packet);

                    if (sendRet >= 0 || sendRet == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    {
                        while (true)
                        {
                            ffmpeg.av_frame_unref(_frame);
                            int recvRet = ffmpeg.avcodec_receive_frame(_codecContext, _frame);

                            if (recvRet == ffmpeg.AVERROR_EOF)
                            {
                                _isEndOfFile = true;
                                break;
                            }

                            if (recvRet == ffmpeg.AVERROR(ffmpeg.EAGAIN) || recvRet < 0) break;

                            long pts = _frame->best_effort_timestamp;
                            if (pts == ffmpeg.AV_NOPTS_VALUE) pts = _frame->pts;

                            if (fastSeek || pts >= targetPts)
                            {
                                _hasBufferedFrame = true;
                                ffmpeg.av_packet_unref(_packet);
                                return;
                            }
                        }
                    }
                }
                ffmpeg.av_packet_unref(_packet);
            }

            _isEndOfFile = true;
        }

        public void SeekAccurate(TimeSpan timePosition)
        {
            if (_disposed || _videoStreamIndex < 0) return;

            AVStream* stream = _formatContext->streams[_videoStreamIndex];
            long startPts = stream->start_time;
            if (startPts == ffmpeg.AV_NOPTS_VALUE) startPts = 0;

            // ИСПРАВЛЕНИЕ: Точный расчет целевого PTS для точного Seek
            AVRational targetTimeBase = new() { num = 1, den = 1000 };
            long offsetPts = ffmpeg.av_rescale_q((long)timePosition.TotalMilliseconds, targetTimeBase, stream->time_base);
            long targetPts = startPts + offsetPts;

            long currentPts = _frame != null ? _frame->best_effort_timestamp : ffmpeg.AV_NOPTS_VALUE;
            if (currentPts == ffmpeg.AV_NOPTS_VALUE && _frame != null) currentPts = _frame->pts;
            if (currentPts == ffmpeg.AV_NOPTS_VALUE) currentPts = 0;

            double diffSec = (targetPts - currentPts) * _timeBase;

            if (diffSec < -0.05 || diffSec > 0.5 || currentPts == 0 || _isEndOfFile)
            {
                ffmpeg.av_seek_frame(_formatContext, _videoStreamIndex, targetPts, ffmpeg.AVSEEK_FLAG_BACKWARD);
                ffmpeg.avcodec_flush_buffers(_codecContext);
                _isEndOfFile = false;
            }

            _hasBufferedFrame = false;

            while (ffmpeg.av_read_frame(_formatContext, _packet) >= 0)
            {
                if (_packet->stream_index == _videoStreamIndex)
                {
                    int sendRet = ffmpeg.avcodec_send_packet(_codecContext, _packet);
                    while (sendRet >= 0 || sendRet == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    {
                        ffmpeg.av_frame_unref(_frame);
                        int recvRet = ffmpeg.avcodec_receive_frame(_codecContext, _frame);

                        if (recvRet == ffmpeg.AVERROR_EOF)
                        {
                            _isEndOfFile = true;
                            break;
                        }
                        if (recvRet == ffmpeg.AVERROR(ffmpeg.EAGAIN)) break;

                        if (recvRet >= 0)
                        {
                            long pts = _frame->best_effort_timestamp;
                            if (pts == ffmpeg.AV_NOPTS_VALUE) pts = _frame->pts;

                            if (pts >= targetPts)
                            {
                                _hasBufferedFrame = true;
                                ffmpeg.av_packet_unref(_packet);
                                return;
                            }
                        }
                        if (sendRet == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                            sendRet = ffmpeg.avcodec_send_packet(_codecContext, _packet);
                    }
                }
                ffmpeg.av_packet_unref(_packet);
            }

            _isEndOfFile = true;
        }

        public bool TryReadNextFrame(IntPtr targetBufferPtr, int targetWidth, int targetHeight, int targetPitch, out double ptsMs)
        {
            ptsMs = 0;
            if (_disposed || _videoStreamIndex < 0) return false;

            if (_hasBufferedFrame)
            {
                ptsMs = GetPtsMs(_frame);
                ProcessAndScaleFrame(targetBufferPtr, targetWidth, targetHeight, targetPitch);
                _hasBufferedFrame = false;
                return true;
            }

            if (ReceiveAndScale(targetBufferPtr, targetWidth, targetHeight, targetPitch, out ptsMs)) return true;

            while (ffmpeg.av_read_frame(_formatContext, _packet) >= 0)
            {
                if (_packet->stream_index == _videoStreamIndex)
                {
                    int sendRet = ffmpeg.avcodec_send_packet(_codecContext, _packet);
                    while (sendRet >= 0 || sendRet == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    {
                        ffmpeg.av_frame_unref(_frame);
                        int recvRet = ffmpeg.avcodec_receive_frame(_codecContext, _frame);

                        if (recvRet == ffmpeg.AVERROR_EOF)
                        {
                            _isEndOfFile = true;
                            break;
                        }
                        if (recvRet == ffmpeg.AVERROR(ffmpeg.EAGAIN)) break;

                        if (recvRet >= 0)
                        {
                            ptsMs = GetPtsMs(_frame);
                            ProcessAndScaleFrame(targetBufferPtr, targetWidth, targetHeight, targetPitch);
                            ffmpeg.av_packet_unref(_packet);
                            return true;
                        }
                        if (sendRet == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                            sendRet = ffmpeg.avcodec_send_packet(_codecContext, _packet);
                    }
                }
                ffmpeg.av_packet_unref(_packet);
            }

            _isEndOfFile = true;

            ffmpeg.avcodec_send_packet(_codecContext, null);
            return ReceiveAndScale(targetBufferPtr, targetWidth, targetHeight, targetPitch, out ptsMs);
        }

        private bool ReceiveAndScale(IntPtr targetBufferPtr, int targetWidth, int targetHeight, int targetPitch, out double ptsMs)
        {
            ptsMs = 0;
            ffmpeg.av_frame_unref(_frame);

            int recvRet = ffmpeg.avcodec_receive_frame(_codecContext, _frame);
            if (recvRet == ffmpeg.AVERROR_EOF) _isEndOfFile = true;

            if (recvRet >= 0)
            {
                ptsMs = GetPtsMs(_frame);
                ProcessAndScaleFrame(targetBufferPtr, targetWidth, targetHeight, targetPitch);
                return true;
            }
            return false;
        }

        private void ProcessAndScaleFrame(IntPtr targetBufferPtr, int targetWidth, int targetHeight, int targetPitch)
        {
            AVFrame* frameToScale = _frame;
            bool isHwTransferred = false;

            if (_frame->hw_frames_ctx != null || _frame->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11)
            {
                try
                {
                    if (ffmpeg.av_hwframe_transfer_data(_swFrame, _frame, 0) >= 0)
                    {
                        _swFrame->pts = _frame->pts;
                        _swFrame->best_effort_timestamp = _frame->best_effort_timestamp;
                        frameToScale = _swFrame;
                        isHwTransferred = true;
                    }
                }
                catch
                {
                    ffmpeg.av_frame_unref(_swFrame);
                    throw;
                }
            }

            try
            {
                ConvertAndScaleFrame(frameToScale, targetBufferPtr, targetWidth, targetHeight, targetPitch);
            }
            finally
            {
                if (isHwTransferred)
                {
                    ffmpeg.av_frame_unref(_swFrame);
                }
            }
        }

        private void ConvertAndScaleFrame(AVFrame* srcFrame, IntPtr targetBufferPtr, int targetWidth, int targetHeight, int targetPitch)
        {
            const int swsFastBilinear = 1;
            _swsContext = ffmpeg.sws_getCachedContext(
                _swsContext,
                srcFrame->width, srcFrame->height, (AVPixelFormat)srcFrame->format,
                targetWidth, targetHeight, AVPixelFormat.AV_PIX_FMT_BGRA,
                swsFastBilinear, null, null, null);

            byte*[] dstData = new byte*[8];
            int[] dstLinesize = new int[8];

            dstData[0] = (byte*)targetBufferPtr.ToPointer();
            dstLinesize[0] = targetPitch;

            fixed (byte** pData = dstData)
            fixed (int* pLinesize = dstLinesize)
            {
                ffmpeg.sws_scale(
                    _swsContext,
                    srcFrame->data,
                    srcFrame->linesize,
                    0,
                    srcFrame->height,
                    dstData,
                    dstLinesize);
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private static void SafeReleaseNative(
            IntPtr pSws, IntPtr pFrame, IntPtr pSwFrame, IntPtr pPacket,
            IntPtr pCodecCtx, IntPtr pHwFrames, IntPtr pHwDev, IntPtr pFormatCtx)
        {
            try
            {
                if (pSws != IntPtr.Zero) ffmpeg.sws_freeContext((SwsContext*)pSws);

                if (pFrame != IntPtr.Zero)
                {
                    AVFrame* frame = (AVFrame*)pFrame;
                    ffmpeg.av_frame_free(&frame);
                }

                if (pSwFrame != IntPtr.Zero)
                {
                    AVFrame* swFrame = (AVFrame*)pSwFrame;
                    ffmpeg.av_frame_free(&swFrame);
                }

                if (pPacket != IntPtr.Zero)
                {
                    AVPacket* packet = (AVPacket*)pPacket;
                    ffmpeg.av_packet_free(&packet);
                }

                if (pCodecCtx != IntPtr.Zero)
                {
                    AVCodecContext* codecCtx = (AVCodecContext*)pCodecCtx;
                    ffmpeg.avcodec_free_context(&codecCtx);
                }

                if (pHwFrames != IntPtr.Zero)
                {
                    AVBufferRef* hwFrames = (AVBufferRef*)pHwFrames;
                    ffmpeg.av_buffer_unref(&hwFrames);
                }

                if (pHwDev != IntPtr.Zero)
                {
                    AVBufferRef* hwDev = (AVBufferRef*)pHwDev;
                    ffmpeg.av_buffer_unref(&hwDev);
                }

                if (pFormatCtx != IntPtr.Zero)
                {
                    AVFormatContext* formatCtx = (AVFormatContext*)pFormatCtx;
                    ffmpeg.avformat_close_input(&formatCtx);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NativeMediaDecoder] Ошибка фоновой очистки: {ex.Message}");
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            IntPtr pSws = (IntPtr)_swsContext;
            IntPtr pFrame = (IntPtr)_frame;
            IntPtr pSwFrame = (IntPtr)_swFrame;
            IntPtr pPacket = (IntPtr)_packet;
            IntPtr pCodecCtx = (IntPtr)_codecContext;
            IntPtr pHwFrames = (IntPtr)_hwFramesCtx;
            IntPtr pHwDev = (IntPtr)_hwDeviceCtx;
            IntPtr pFormatCtx = (IntPtr)_formatContext;

            _swsContext = null;
            _frame = null;
            _swFrame = null;
            _packet = null;
            _codecContext = null;
            _hwFramesCtx = null;
            _hwDeviceCtx = null;
            _formatContext = null;

            if (disposing)
            {
                SafeReleaseNative(pSws, pFrame, pSwFrame, pPacket, pCodecCtx, pHwFrames, pHwDev, pFormatCtx);
            }
            else
            {
                if (!Environment.HasShutdownStarted)
                {
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        SafeReleaseNative(pSws, pFrame, pSwFrame, pPacket, pCodecCtx, pHwFrames, pHwDev, pFormatCtx);
                    });
                }
            }

            _disposed = true;
        }
    }
}