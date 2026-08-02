' Path: IServices.vb
Option Strict On
Option Explicit On

Imports System
Imports System.Buffers
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.Drawing
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Forms

Public Class IServices

    Public Enum PlaybackState
        Stopped
        Playing
        Paused
        [Error]
    End Enum

    Public Class PooledFrameBuffer
        Implements IDisposable

        Public ReadOnly Buffer As Byte()
        Public ReadOnly Size As Integer
        Private _disposed As Integer = 0

        Public Sub New(buffer As Byte(), size As Integer)
            Me.Buffer = buffer
            Me.Size = size
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If Interlocked.Exchange(_disposed, 1) = 0 Then
                If Buffer IsNot Nothing Then
                    ArrayPool(Of Byte).Shared.Return(Buffer)
                End If
            End If
            GC.SuppressFinalize(Me)
        End Sub

        Protected Overrides Sub Finalize()
            Dispose()
            MyBase.Finalize()
        End Sub
    End Class

    <System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)>
    Public Structure PeakMinMax
        Public MinL As SByte
        Public MaxL As SByte
        Public MinR As SByte
        Public MaxR As SByte
    End Structure

    Public Interface IFFmpegService
        Inherits IDisposable
        Event LogMessage As Action(Of String)
        Property ActiveFFmpegProcess As Process

        Function GetFFmpegPath() As String
        Function GetFFprobePath() As String
        Function CheckFFmpeg() As Boolean
        Function GetMediaInfoAsync(filePath As String) As Task(Of FFmpegService.MediaInfo)
        Function RunProcessCaptureAsync(exePath As String, arguments As String, timeoutMs As Integer, token As CancellationToken) As Task(Of FFmpegService.ProcessResult)
        Function StartFFmpegWithProgressAsync(arguments As String, targetDurationSec As Double, progressReporter As IProgress(Of FFmpegService.FFmpegProgress), token As CancellationToken) As Task(Of Integer)
        Function ExtractPreviewFrameFromPipeAsync(videoFilePath As String, timePosition As TimeSpan, targetWidth As Integer, targetHeight As Integer, token As CancellationToken) As Task(Of PooledFrameBuffer)
        Function BakeAudioShiftAsync(inputPath As String, outputPath As String, offset As TimeSpan, token As CancellationToken) As Task(Of Boolean)
        Function ExtractFramePooledAsync(videoFilePath As String, timePosition As TimeSpan, width As Integer, height As Integer, token As CancellationToken) As Task(Of PooledFrameBuffer)
        Function ExtractAudioToWavAsync(inputPath As String, outputPath As String, token As CancellationToken) As Task(Of Boolean)
        Function GenerateAudioPeaksAsync(inputPath As String, samplesPerPeak As Integer, token As CancellationToken) As Task(Of PeakMinMax())
    End Interface

    Public Interface IHardwareMonitorService
        Inherits IDisposable
        Function ScanHardwareAsync() As Task(Of HardwareMonitorService.HardwareScanResult)
        Function GetNvidiaGeneration(gpuName As String) As Integer
        Function GetAmdGeneration(gpuName As String) As Integer
    End Interface

    Public Class CutRegionData
        Public ReadOnly StartTime As TimeSpan
        Public ReadOnly EndTime As TimeSpan
        Public Sub New(s As TimeSpan, e As TimeSpan)
            StartTime = s
            EndTime = e
        End Sub
    End Class

    Public Class TrackSnapshot
        Public Id As Guid
        Public Name As String
        Public Type As TargetFormatType
        Public IsMuted As Boolean
        Public Clips As IReadOnlyList(Of MediaClip)
    End Class

    Public Class TimelineStateData
        Public Duration As TimeSpan
        Public AudioDuration As TimeSpan
        Public MarkerStart As TimeSpan
        Public MarkerEnd As TimeSpan
        Public IsZoomed As Boolean
        Public ViewStart As TimeSpan
        Public ViewEnd As TimeSpan
        Public CutRegions As IEnumerable(Of CutRegionData)

        ' ИСПРАВЛЕНИЕ: Иммутабельный снимок треков для потокобезопасного рендеринга
        Public Tracks As IReadOnlyList(Of TrackSnapshot)
    End Class

    Public Interface ITimelineRenderer
        Inherits IDisposable
        Event PlayheadScrubbed As Action(Of TimeSpan)
        Event PlayheadSeekCompleted As Action(Of TimeSpan)
        Event MarkerStartChanged As Action(Of TimeSpan)
        Event MarkerEndChanged As Action(Of TimeSpan)
        Event MarkersCommit As Action
        Event AudioOffsetChanged As Action(Of TimeSpan)
        Event AudioOffsetCommit As Action(Of TimeSpan)
        Event PreviewRequested As Action(Of TimeSpan)
        Event PlaybackPauseRequested As Action
        Event CursorMoved As Action(Of TimeSpan, Integer)
        Event CursorLeft As Action
        Event DeviceRecreated As Action
        Event LogMessage As Action(Of String)

        Property IsDarkTheme As Boolean
        Property AudioFadeIn As TimeSpan
        Property AudioFadeOut As TimeSpan
        Property TrackVolume As Single

        Sub Initialize(pb As Control)
        Sub Resize(width As Integer, height As Integer)
        Sub UpdateLayout(newTileSize As Size, newTileCount As Integer)
        Function LoadStripAsync(targetIndex As Integer, tempFilePath As String) As Task
        Sub ClearStrips()
        Sub UpdatePreviewFromRawBytes(rawBytes() As Byte, width As Integer, height As Integer)
        Sub UpdateState(state As TimelineStateData, fps As Double, hasSelection As Boolean, isAudioReplaced As Boolean, hasAudio As Boolean)
        Sub UpdatePlayhead(timePosition As TimeSpan)
        Sub UpdateAudioOffset(offset As TimeSpan, bakedOffset As TimeSpan)
        Sub UpdateLoadingState(isLoading As Boolean, rotAngle As Single, fadeAlpha As Single)
        Sub SetDataSources(caches As Object, extractors As Object)
        Function GetThumbSize() As Size
        Sub SetAudioPeaks(peaks() As PeakMinMax, samplesPerPeak As Integer)
    End Interface

    Public Interface IMediaPlayerManager
        Inherits IDisposable
        Event PlaybackError As Action(Of String)
        Event PlaybackStopped As EventHandler
        Event LengthChanged As Action(Of TimeSpan)
        Event LogMessage As Action(Of String)

        Property ExternalClock As Func(Of TimeSpan)

        ReadOnly Property IsPlaying As Boolean
        ReadOnly Property CurrentTimeMs As Long
        ReadOnly Property State As PlaybackState
        Property Volume As Integer
        Property Rate As Single

        Sub SetVolume(percent As Integer)
        Sub Initialize(basePath As String, videoView As Control)
        Function PlayAsync(filePath As String, startTime As TimeSpan, Optional externalAudioPath As String = "", Optional videoFilter As String = "") As Task(Of Boolean)
        Sub Play(filePath As String, Optional externalAudioPath As String = "")
        Sub StopPlayback()
        Sub Pause()
        Sub ResumePlayback()
        Sub Seek(targetTime As TimeSpan)
        Sub SetAudioDelay(delayUs As Long)
        Sub SetVideoOpacity(opacity As Single)
    End Interface

    Public Interface IAudioPlayer
        Inherits IDisposable
        ReadOnly Property IsPlaying As Boolean
        Property Volume As Integer

        ReadOnly Property OutputLatencyMs As Double

        Function GetCurrentPosition() As TimeSpan
        Sub Play(physTime As TimeSpan, offset As TimeSpan)
        Sub Pause()
        Sub ResumePlayback()
        Sub StopPlayback()
        Sub Seek(physTime As TimeSpan, offset As TimeSpan)
        Sub UnloadFile()
        Sub LoadStreaming(filePath As String)
    End Interface

    Public Interface IPlaybackController
        Inherits IDisposable
        Event TimeChanged As EventHandler(Of TimeSpan)
        Event PlaybackStopped As EventHandler
        Event PlaybackError As EventHandler(Of String)
        Event MarkerReached As EventHandler

        ReadOnly Property State As PlaybackState
        ReadOnly Property IsPlaying As Boolean
        ReadOnly Property CurrentVirtualTime As TimeSpan
        ReadOnly Property CurrentPhysicalTime As TimeSpan

        Function PlayAsync(filePath As String, startVirtualTime As TimeSpan, Optional externalAudio As String = "", Optional cancellationToken As CancellationToken = Nothing) As Task
        Sub Pause()
        Sub ResumePlayback()
        Sub StopPlayback()
        Sub Seek(virtualTime As TimeSpan)
        Sub SetVolume(percent As Integer)
        Sub SetRate(rate As Single)
        Sub SetAudioOffset(offset As TimeSpan)
        Sub SetAudioDelay(delayUs As Long)
        Sub ProcessTick()
    End Interface
End Class