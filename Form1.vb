Option Strict On
Option Explicit On

Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.ComponentModel
Imports System.Diagnostics
Imports System.Drawing
Imports System.Drawing.Imaging
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports LibreHardwareMonitor.Interop.PowerMonitor
Imports Serilog
Imports Windows.UI.ViewManagement
Imports yoump.IServices
Imports System.Runtime.InteropServices
Imports yoump.Native

Public Class Form1
    Implements IMainEditorView

    Private _isDarkTheme As Boolean = True
    Public FrameQualityMultiplier As Single = 3.0F
    Public IsCropModeActive As Boolean = True
    Private _isDraggingCrop As Boolean = False
    Private _startCropPoint As Point
    Private _currentCropRect As System.Drawing.Rectangle
    Public FinalCropX As Integer = 0
    Public FinalCropY As Integer = 0
    Public FinalCropW As Integer = 0
    Public FinalCropH As Integer = 0
    Private _model As ProjectModel
    Private _presenter As MainEditorPresenter

    Private _playbackController As IPlaybackController
    Private _audioPlayer As IAudioPlayer

    Private ReadOnly _videoPlayerSyncLock As New Object()
    Private ReadOnly _textChangeSemaphore As New SemaphoreSlim(1, 1)
    Private _lastRateChangeTicks As Long = 0
    Private Const RATE_CHANGE_COOLDOWN_MS As Long = 500
    Private ReadOnly _previewCache As New PreviewCacheManager(30)

    Private ReadOnly generateLock As New Object()
    Private ReadOnly seekTimeoutStopwatch As New Stopwatch()
    Private ReadOnly mediaStopLock As New Object()
    Private ReadOnly tcsLock As New Object()
    Private isNvidiaGpuSelected As Boolean = False
    Private isAMDGpuSelected As Boolean = False
    Private lastHardwareIndex As Integer = 0
    Private wasStoppedByUser As Boolean = False
    Private isEncodersLoaded As Boolean = False
    Private mediaStopTcs As TaskCompletionSource(Of Boolean)
    Private mediaStopTask As Task = Task.CompletedTask

    Private selectedFiles As New List(Of String)()
    Private availableEncoders As List(Of String) = Nothing
    Private isClosing As Boolean = False

    Private inputHasImage As Boolean = False
    Private inputHasAudio As Boolean = False
    Private inputHasVideoWithAudio As Boolean = False
    Private inputHasVideoNoAudio As Boolean = False

    Private popoutForm As Form2 = Nothing
    Private playbackStopwatch As New Stopwatch()
    Private playbackStartTime As TimeSpan = TimeSpan.Zero
    Private pendingPreviewTime As TimeSpan = TimeSpan.Zero
    Private _lastPreviewTime As TimeSpan = TimeSpan.Zero
    Private previewCts As CancellationTokenSource = Nothing
    Private generateContactCts As CancellationTokenSource = Nothing
    Private resizeInProgress As Boolean = False
    Private lastStatusUpdateTime As DateTime = DateTime.MinValue

    Private WithEvents LoadingTimer As New System.Windows.Forms.Timer() With {.Interval = 16}
    Private ReadOnly _loadingStopwatch As New Stopwatch()
    Private rotAngle As Single = 0
    Private fadeAlpha As Single = 0
    Private isProLoading As Boolean = False

    Private loadingCount As Integer = 0
    Private currentInputType As MediaType = MediaType.Video
    Private _currentMediaInfo As FFmpegService.MediaInfo
    Private _gpuResult As HardwareMonitorService.HardwareScanResult
    Private _textChangeCts As CancellationTokenSource = Nothing
    Private _previewGenerationRevision As Long = 0
    Private WithEvents InternalPreviewBox As PictureBox
    Private _externalAudioPath As String = String.Empty
    Private _isAudioReplaced As Boolean = False
    Private _isCleanupComplete As Boolean = False

    Private _audioOffset As TimeSpan = TimeSpan.Zero
    Private _bakedAudioOffset As TimeSpan = TimeSpan.Zero

    Private ReadOnly _proLoadingLock As New Object()
    Private _wasPlayingBeforeSeek As Boolean = False
    Private _pendingSeekTime As TimeSpan = TimeSpan.Zero
    Private _isPaused As Boolean = False

    Private _hoverVirtualTime As TimeSpan? = Nothing
    Private _currentVirtualPlaybackTime As TimeSpan = TimeSpan.Zero

    Private WithEvents PopoutAnimTimer As New System.Windows.Forms.Timer() With {.Interval = 16}
    Private ReadOnly _popoutStopwatch As New Stopwatch()
    Private popoutRotAngle As Single = 0
    Private popoutFadeAlpha As Single = 0

    Private _virtualPlayhead As TimeSpan = TimeSpan.Zero
    Private _isTrackingPlayhead As Boolean = False

    Private _currentBakedAudioPath As String = String.Empty
    Private _isAudioBaked As Boolean = False
    Private _audioBakeCts As CancellationTokenSource = Nothing

    Private _proxyVideoCachePath As String = String.Empty
    Private _isPlayPauseProcessing As Boolean = False
    Private _awaitingFirstValidTime As Boolean = False
    Private _masterVolumeCache As Integer = 100

    Private _trackVolume As Single = 1.0F
    Private _tileRendererRef As TileTimelineRenderer
    Private _scrubCts As CancellationTokenSource = Nothing
    Private _lastScrubTime As DateTime = DateTime.MinValue
    Private Const ScrubThrottleMs As Integer = 12
    Private _wasPlayingBeforeScrub As Boolean = False
    Private _currentFps As Double = 30.0
    Private _currentFilePath As String = String.Empty
    Private _directPlayer As Direct3D11VideoPlayer

    Private _scrubTargetTime As TimeSpan = TimeSpan.MinValue
    Private _lastDecodedScrubTime As TimeSpan = TimeSpan.MinValue
    Private _isScrubLoopRunning As Boolean = False
    Private _scrubLoopCts As CancellationTokenSource
    Private _currentAudioEngineName As String = "XAudio2"
    Private _fadeStopwatch As Stopwatch = Stopwatch.StartNew()
    Private _lastFadeTickSec As Double = 0.0
    Private _smoothedOpacity As Single = 1.0F

    Private _gpuFramePool As GpuFramePool
    Private ReadOnly _gpuFrameCaches As New Dictionary(Of String, Object)()
    Private ReadOnly _gpuFrameExtractors As New Dictionary(Of String, Object)()
    Private _asyncDecoder As AsyncMediaDecoder
    Private _scrubFramePool As GpuFramePool

    Private _inspectorForm As Form4



    Private Shared ReadOnly ImageExtensions As String() = {".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".avif", ".jxl"}
    Private Shared ReadOnly AudioExtensions As String() = {".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg"}
    Private Shared ReadOnly VideoExtensions As String() = {".mp4", ".mkv", ".avi", ".mov", ".flv", ".ts", ".wmv", ".webm", ".m4v"}

    Private Enum MediaType
        Video
        Audio
        Image
    End Enum

    Public Class ResolutionProfile
        Public Property Name As String
        Public Property Width As Integer
        Public Property Height As Integer
        Public Overrides Function ToString() As String
            Return Name
        End Function
    End Class

    Private ReadOnly ResolutionProfiles As New List(Of ResolutionProfile) From {
        New ResolutionProfile With {.Name = "Оригинал (Без изменений)", .Width = 0, .Height = 0},
        New ResolutionProfile With {.Name = "4K UHD (3840x2160) - 16:9", .Width = 3840, .Height = 2160},
        New ResolutionProfile With {.Name = "2K QHD (2560x1440) - 16:9", .Width = 2560, .Height = 1440},
        New ResolutionProfile With {.Name = "Full HD (1920x1080) - 16:9", .Width = 1920, .Height = 1080},
        New ResolutionProfile With {.Name = "HD (1280x720) - 16:9", .Width = 1280, .Height = 720},
        New ResolutionProfile With {.Name = "4K Vertical (2160x3840) - 9:16", .Width = 2160, .Height = 3840},
        New ResolutionProfile With {.Name = "Full HD Vertical (1080x1920) - 9:16", .Width = 1080, .Height = 1920},
        New ResolutionProfile With {.Name = "HD Vertical (720x1280) - 9:16", .Width = 720, .Height = 1280},
        New ResolutionProfile With {.Name = "Tablet Landscape (1024x768) - 4:3", .Width = 1024, .Height = 768},
        New ResolutionProfile With {.Name = "Tablet Portrait (768x1024) - 3:4", .Width = 768, .Height = 1024},
        New ResolutionProfile With {.Name = "Square HD (1080x1080) - 1:1", .Width = 1080, .Height = 1080}
    }

    Private Shared ReadOnly _formats() As String = {
        "MP4 (MPEG-4)", "MKV (Matroska)", "AVI (Audio Video Interleave)",
        "MOV (QuickTime)", "FLV (Flash Video)", "TS (Transport Stream)",
        "WMV (Windows Media Video)", "WebM (VP9/AV1)", "M4V (iTunes Video)",
        "Audio MP3", "Audio WAV", "Audio AAC", "Audio FLAC",
        "Audio OGG (Vorbis)", "Audio Opus", "Audio AC3 (Dolby Digital)",
        "Image GIF", "Image WebP", "Image PNG", "Image JPG",
        "Image AVIF", "Image JPEG XL", "Image BMP"
    }

    Private Shared ReadOnly LegacyAndCpuOnlyCodecs As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
        "WMV2", "WMV1", "MPEG-4", "Xvid", "DivX",
        "VP6", "FLV1", "VP9", "ProRes", "H.266", "DNxHR", "CineForm", "MPEG-2", "VP8",
        "MP3", "WAV", "AAC", "FLAC", "Vorbis", "Opus", "AC3",
        "GIF", "WebP", "PNG", "JPG", "AVIF", "JPEG XL", "BMP"
    }

    Private ReadOnly FormatToCodecs As New Dictionary(Of String, List(Of String)) From {
        {"MP4 (MPEG-4)", New List(Of String) From {"H.264", "H.265", "H.266", "MPEG-4", "VP9", "AV1"}},
        {"MKV (Matroska)", New List(Of String) From {"H.264", "H.265", "H.266", "MPEG-4", "VP9", "AV1", "ProRes", "DNxHR"}},
        {"AVI (Audio Video Interleave)", New List(Of String) From {"H.264", "MPEG-4", "Xvid", "DivX", "CineForm"}},
        {"MOV (QuickTime)", New List(Of String) From {"H.264", "H.265", "H.266", "ProRes", "DNxHR", "CineForm"}},
        {"FLV (Flash Video)", New List(Of String) From {"H.264", "H.265", "FLV1"}},
        {"TS (Transport Stream)", New List(Of String) From {"H.264", "H.265", "H.266", "MPEG-2"}},
        {"WMV (Windows Media Video)", New List(Of String) From {"H.264", "WMV2", "WMV1"}},
        {"WebM (VP9/AV1)", New List(Of String) From {"VP9", "AV1", "VP8"}},
        {"M4V (iTunes Video)", New List(Of String) From {"H.264", "H.265", "H.266"}},
        {"Audio MP3", New List(Of String) From {"MP3"}},
        {"Audio WAV", New List(Of String) From {"WAV"}},
        {"Audio AAC", New List(Of String) From {"AAC"}},
        {"Audio FLAC", New List(Of String) From {"FLAC"}},
        {"Audio OGG (Vorbis)", New List(Of String) From {"Vorbis"}},
        {"Audio Opus", New List(Of String) From {"Opus"}},
        {"Audio AC3 (Dolby Digital)", New List(Of String) From {"AC3"}},
        {"Image GIF", New List(Of String) From {"GIF"}},
        {"Image WebP", New List(Of String) From {"WebP"}},
        {"Image PNG", New List(Of String) From {"PNG"}},
        {"Image JPG", New List(Of String) From {"JPG"}},
        {"Image AVIF", New List(Of String) From {"AVIF"}},
        {"Image JPEG XL", New List(Of String) From {"JPEG XL"}},
        {"Image BMP", New List(Of String) From {"BMP"}}
    }

    Private Shared ReadOnly CodecToBaseEncoder As New Dictionary(Of String, String) From {
        {"H.264", "libx264"},
        {"H.265", "libx265"},
        {"H.266", "libvvenc"},
        {"AV1", "libsvtav1"},
        {"VP9", "libvpx-vp9"},
        {"VP8", "libvpx"},
        {"ProRes", "prores_ks"},
        {"DNxHR", "dnxhd"},
        {"CineForm", "cfhd"},
        {"MPEG-4", "mpeg4"},
        {"MPEG-2", "mpeg2video"},
        {"Xvid", "libxvid"},
        {"DivX", "libxvid"},
        {"FLV1", "flv"},
        {"WMV2", "wmv2"},
        {"WMV1", "wmv1"},
        {"MP3", "libmp3lame"},
        {"WAV", "pcm_s16le"},
        {"AAC", "aac"},
        {"FLAC", "flac"},
        {"Vorbis", "libvorbis"},
        {"Opus", "libopus"},
        {"AC3", "ac3"},
        {"GIF", "gif"},
        {"WebP", "libwebp"},
        {"PNG", "png"},
        {"JPG", "mjpeg"},
        {"AVIF", "libsvtav1"},
        {"JPEG XL", "libjxl"},
        {"BMP", "bmp"}
    }
    Private Class MarkerData
        Public StartTime As TimeSpan
        Public EndTime As TimeSpan
        Public IsZoomed As Boolean
        Public ViewStart As TimeSpan
        Public ViewEnd As TimeSpan
    End Class

    Private ReadOnly fileMarkers As New ConcurrentDictionary(Of String, MarkerData)()
    Private lastPreviewRequest As DateTime = DateTime.MinValue
    Private Const PreviewThrottleMs As Integer = 200

    Private originalVideoViewBounds As System.Drawing.Rectangle
    Private originalVideoViewParent As Control

    Private ReadOnly _ffmpegService As FFmpegService
    Private ReadOnly _videoPlayer As IMediaPlayerManager
    Private ReadOnly _hardwareMonitor As HardwareMonitorService
    Private ReadOnly _fileManager As FileManager

    Private _tileRenderer As ITimelineRenderer
    Private Const InternalPreviewBoxName As String = "InternalPreviewBox"



    Private ReadOnly _audioDragStartOffset As TimeSpan
    Private ReadOnly TimelinePadding As Integer
    Private isNvidiaGpu As Boolean = False
    Private isAmdGpu As Boolean = False
    Private isCpuSelected As Boolean = True
    Private _playbackLoopCts As CancellationTokenSource
    Private _isPlaybackLoopRunning As Boolean = False


    Public Event CutRequested As EventHandler Implements IMainEditorView.CutRequested
    Public Event ClearCutsRequested As EventHandler Implements IMainEditorView.ClearCutsRequested
    Public Event ZoomInRequested As EventHandler Implements IMainEditorView.ZoomInRequested
    Public Event ZoomOutRequested As EventHandler Implements IMainEditorView.ZoomOutRequested
    Public Event PlaybackTick As EventHandler(Of Long) Implements IMainEditorView.PlaybackTick

    <Browsable(False)>
    <DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property MarkerStart As TimeSpan Implements IMainEditorView.MarkerStart
        Get
            Return _model.MarkerStart
        End Get
        Set(value As TimeSpan)
            _model.MarkerStart = value
        End Set
    End Property

    <Browsable(False)>
    <DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property MarkerEnd As TimeSpan Implements IMainEditorView.MarkerEnd
        Get
            Return _model.MarkerEnd
        End Get
        Set(value As TimeSpan)
            _model.MarkerEnd = value
        End Set
    End Property

    Public Sub New()
        ThemeManager.LoadSettings()
        _isDarkTheme = ThemeManager.IsDarkTheme

        If _isDarkTheme Then
            Application.SetColorMode(SystemColorMode.Dark)
        Else
            Application.SetColorMode(SystemColorMode.Classic)
        End If

        InitializeComponent()
    End Sub

    Public Sub New(ffmpeg As FFmpegService, videoPlayer As IMediaPlayerManager, hardwareMonitor As HardwareMonitorService)
        Me.New()
        _ffmpegService = ffmpeg
        _videoPlayer = videoPlayer
        _hardwareMonitor = hardwareMonitor
        _fileManager = New FileManager(Application.StartupPath)
    End Sub

    Private Sub DeleteProxyVideoFile()
        If Not String.IsNullOrEmpty(_proxyVideoCachePath) AndAlso IO.File.Exists(_proxyVideoCachePath) Then
            Try
                IO.File.Delete(_proxyVideoCachePath)
            Catch ex As Exception
                SafeLog("Предупреждение: Не удалось удалить временное прокси-видео: " & ex.Message)
            End Try
            _proxyVideoCachePath = String.Empty
        End If
    End Sub

    Private Sub ClearAllGpuCaches()
        For Each kvp In _gpuFrameExtractors
            Dim ext = TryCast(kvp.Value, GpuFrameExtractor)
            If ext IsNot Nothing Then
                ext.CancelAll()
                ext.Dispose()
            End If
        Next
        _gpuFrameExtractors.Clear()

        For Each kvp In _gpuFrameCaches
            Dim cache = TryCast(kvp.Value, GpuFrameCacheManager)
            If cache IsNot Nothing Then cache.Dispose()
        Next
        _gpuFrameCaches.Clear()
    End Sub

    Private Sub StartPlaybackUIUpdateLoop()
        If _isPlaybackLoopRunning Then Return
        _isPlaybackLoopRunning = True

        _playbackLoopCts = New CancellationTokenSource()
        Dim token = _playbackLoopCts.Token

        Task.Run(Async Function() As Task
                     Try
                         Dim delayMs As Integer = 15

                         While Not token.IsCancellationRequested
                             If Not isClosing AndAlso _playbackController IsNot Nothing Then
                                 _playbackController.ProcessTick()
                             End If

                             Await Task.Delay(delayMs, token)
                         End While
                     Catch ex As OperationCanceledException
                     Catch ex As Exception
                         SafeLog("Ошибка в асинхронном цикле плейхеда: " & ex.Message)
                     Finally
                         _isPlaybackLoopRunning = False
                     End Try
                 End Function)
    End Sub

    Private Sub StopPlaybackUIUpdateLoop()
        If _playbackLoopCts IsNot Nothing Then
            Try
                _playbackLoopCts.Cancel()
                _playbackLoopCts.Dispose()
            Catch
            End Try
            _playbackLoopCts = Nothing
        End If
        _isPlaybackLoopRunning = False
    End Sub

    Private Async Function ExtractProxyVideoIfNeededAsync() As Task(Of Boolean)
        DeleteProxyVideoFile()

        Try
            Dim tempDir As String = IO.Path.GetTempPath()
            Dim oldProxies = IO.Directory.GetFiles(tempDir, "proxy_video_*.mp4")
            For Each oldProxy In oldProxies
                Try
                    IO.File.Delete(oldProxy)
                Catch ex As Exception
                End Try
            Next
        Catch ex As Exception
        End Try

        If selectedFiles Is Nothing OrElse selectedFiles.Count = 0 Then Return False

        Dim currentFile As String = selectedFiles(0)
        Dim ext As String = IO.Path.GetExtension(currentFile).ToLowerInvariant()

        Dim isAnimatedImage As Boolean = {".gif", ".webp"}.Contains(ext) AndAlso (_currentMediaInfo.Duration > TimeSpan.Zero OrElse _currentMediaInfo.Fps > 0)

        If isAnimatedImage Then
            UpdateLabel("Подготовка прокси-видео для предпросмотра анимации...")
            StartProLoading()
            Try
                Dim tempDir As String = IO.Path.GetTempPath()
                _proxyVideoCachePath = IO.Path.Combine(tempDir, $"proxy_video_{Guid.NewGuid():N}.mp4")

                Dim args As String = $"-hide_banner -loglevel error -y -i ""{currentFile}"" -vf ""scale=trunc(iw/2)*2:trunc(ih/2)*2"" -c:v libx264 -preset ultrafast -crf 18 -pix_fmt yuv420p ""{_proxyVideoCachePath}"""

                Using cts As New CancellationTokenSource(TimeSpan.FromSeconds(45))
                    Dim res = Await _ffmpegService.RunProcessCaptureAsync(_ffmpegService.GetFFmpegPath(), args, 45000, cts.Token)
                    If res.ExitCode = 0 AndAlso IO.File.Exists(_proxyVideoCachePath) Then
                        Return True
                    Else
                        _proxyVideoCachePath = String.Empty
                        Return False
                    End If
                End Using
            Catch ex As Exception
                SafeLog("Ошибка генерации прокси-видео: " & ex.Message)
                _proxyVideoCachePath = String.Empty
                Return False
            Finally
                StopProLoading()
            End Try
        End If
        Return False
    End Function

    Protected Overrides Sub OnHandleCreated(e As EventArgs)
        MyBase.OnHandleCreated(e)
        ApplyWindows11NativeLook()
    End Sub

    Private Sub OnGlobalThemeChanged(sender As Object, e As ThemeChangedEventArgs)
        If Me.InvokeRequired Then
            Me.BeginInvoke(New Action(Sub() ApplyTheme(e.IsDark)))
        Else
            ApplyTheme(e.IsDark)
        End If
    End Sub

    Public Sub ApplyTheme(isDark As Boolean)
        _isDarkTheme = isDark

        If _tileRendererRef IsNot Nothing Then
            _tileRendererRef.IsDarkTheme = _isDarkTheme
        End If
        If PictureBox1 IsNot Nothing AndAlso Not PictureBox1.IsDisposed Then
            PictureBox1.Invalidate()
        End If

        ApplyWindows11NativeLook()
    End Sub

    Private Sub ApplyWindows11NativeLook()
        ThemeManager.IsDarkTheme = _isDarkTheme
        ThemeManager.ApplyDwm(Me.Handle)

        If popoutForm IsNot Nothing AndAlso Not popoutForm.IsDisposed Then
            ThemeManager.ApplyDwm(popoutForm.Handle)
            popoutForm.BackColor = ThemeManager.BackColor
            popoutForm.ForeColor = ThemeManager.ForeColor
        End If

        Me.BackColor = ThemeManager.BackColor
        Me.ForeColor = ThemeManager.ForeColor

        Try
            Me.Font = New Font("Segoe UI Variable Display", 9.5F, FontStyle.Regular)
        Catch
            Me.Font = New Font("Segoe UI", 9.5F, FontStyle.Regular)
        End Try

        StyleControls(Me.Controls)
    End Sub

    Private Sub StyleControls(controls As Control.ControlCollection)
        For Each ctrl As Control In controls
            If ctrl.HasChildren Then
                StyleControls(ctrl.Controls)
            End If

            If ctrl.Tag IsNot Nothing AndAlso ctrl.Tag.ToString() = "IgnoreTheme" Then
                Continue For
            End If

            If TypeOf ctrl Is Button Then
                Dim btn As Button = DirectCast(ctrl, Button)
                btn.UseVisualStyleBackColor = False
                btn.FlatAppearance.BorderSize = 0
                btn.FlatStyle = FlatStyle.Flat
                btn.FlatAppearance.BorderSize = 1
                btn.FlatAppearance.BorderColor = If(_isDarkTheme, Color.FromArgb(60, 60, 65), Color.FromArgb(150, 150, 150))
                btn.Cursor = Cursors.Hand
                btn.BackColor = ThemeManager.ControlBackColor
                btn.ForeColor = ThemeManager.ForeColor
                btn.FlatAppearance.MouseOverBackColor = If(_isDarkTheme, Color.FromArgb(62, 62, 66), Color.FromArgb(210, 210, 210))
                btn.FlatAppearance.MouseDownBackColor = Color.FromArgb(0, 120, 215)

                Dim wasEnabled As Boolean = btn.Enabled
                btn.Enabled = Not wasEnabled
                btn.Enabled = wasEnabled

                btn.Invalidate()
                btn.Refresh()

            ElseIf TypeOf ctrl Is Label Then
                Dim lbl As Label = DirectCast(ctrl, Label)
                lbl.BackColor = Color.Transparent
                lbl.ForeColor = If(_isDarkTheme, Color.FromArgb(220, 220, 220), Color.FromArgb(40, 40, 40))
                lbl.Invalidate()

            ElseIf TypeOf ctrl Is ComboBox Then
                Dim cb As ComboBox = DirectCast(ctrl, ComboBox)

                Dim currentStyle = cb.DropDownStyle
                cb.DropDownStyle = ComboBoxStyle.Simple
                cb.DropDownStyle = currentStyle

                cb.FlatStyle = FlatStyle.Flat
                cb.BackColor = ThemeManager.ControlBackColor
                cb.ForeColor = ThemeManager.ForeColor

                cb.Invalidate()
                cb.Refresh()

            ElseIf TypeOf ctrl Is TextBox Then
                Dim tb As TextBox = DirectCast(ctrl, TextBox)
                tb.BorderStyle = BorderStyle.FixedSingle
                tb.BackColor = ThemeManager.ControlBackColor
                tb.ForeColor = ThemeManager.ForeColor
                tb.Invalidate()

            ElseIf TypeOf ctrl Is Panel Then
                Dim pnl As Panel = DirectCast(ctrl, Panel)
                If pnl.BackColor <> Color.Black AndAlso (pnl.Tag Is Nothing OrElse pnl.Tag.ToString() <> "IgnoreTheme") Then
                    pnl.BackColor = ThemeManager.PanelBackColor
                    pnl.Invalidate()
                End If

            ElseIf TypeOf ctrl Is TrackBar Then
                Dim tb As TrackBar = DirectCast(ctrl, TrackBar)
                tb.BackColor = If(TypeOf tb.Parent Is Panel, ThemeManager.PanelBackColor, ThemeManager.BackColor)
                tb.Invalidate()
                tb.Refresh()

            ElseIf TypeOf ctrl Is ToolStrip OrElse TypeOf ctrl Is MenuStrip Then
                Dim ts As ToolStrip = DirectCast(ctrl, ToolStrip)
                ts.Renderer = New ModernToolStripRenderer()
                ts.BackColor = ThemeManager.BackColor
                ts.ForeColor = ThemeManager.ForeColor
                UpdateToolStripItems(ts.Items)
                ts.Invalidate()
                ts.Refresh()

            ElseIf TypeOf ctrl Is ModernProgressBar Then
                ctrl.Invalidate()

            Else
                ctrl.BackColor = ThemeManager.ControlBackColor
                ctrl.ForeColor = ThemeManager.ForeColor
                ctrl.Invalidate()
            End If
        Next
    End Sub

    Private Sub UpdateToolStripItems(items As ToolStripItemCollection)
        For Each item As ToolStripItem In items
            item.ForeColor = ThemeManager.ForeColor
            item.BackColor = ThemeManager.BackColor

            If TypeOf item Is ToolStripDropDownItem Then
                Dim dropDownItem As ToolStripDropDownItem = DirectCast(item, ToolStripDropDownItem)
                UpdateToolStripItems(dropDownItem.DropDownItems)
            End If
        Next
    End Sub

    Public Sub RequestPlayerSeek(physicalTime As TimeSpan) Implements IMainEditorView.RequestPlayerSeek

    End Sub

    Public Sub UpdatePlayheadUI(virtualTime As TimeSpan) Implements IMainEditorView.UpdatePlayheadUI
        _currentVirtualPlaybackTime = virtualTime
        If Not isClosing AndAlso PictureBox1 IsNot Nothing AndAlso Not PictureBox1.IsDisposed Then
            _tileRenderer?.UpdatePlayhead(virtualTime)
        End If
        UpdateDynamicStatus()
    End Sub

    Public Sub StopPlayerUI() Implements IMainEditorView.StopPlayerUI
        _playbackController?.StopPlayback()
        StopPlaybackUIUpdateLoop()
        _awaitingFirstValidTime = False
    End Sub

    Public Sub ShowInfoMessage(text As String) Implements IMainEditorView.ShowInfoMessage
        UpdateLabel(text)
    End Sub

    Public Sub ShowWarningMessage(text As String) Implements IMainEditorView.ShowWarningMessage
        SafeUIInvoke(Sub() MessageBox.Show(text, "Внимание", MessageBoxButtons.OK, MessageBoxIcon.Warning))
    End Sub

    Public Sub RenderTimelineState(stateData As TimelineStateData, fps As Double, hasSelection As Boolean, isAudioReplaced As Boolean, hasAudio As Boolean) Implements IMainEditorView.RenderTimelineState
        If isClosing OrElse _tileRenderer Is Nothing Then Return
        _tileRenderer.UpdateState(stateData, fps, hasSelection, isAudioReplaced, hasAudio)
        _tileRenderer.UpdateAudioOffset(_audioOffset, _bakedAudioOffset)

        If PictureBox1 IsNot Nothing AndAlso Not PictureBox1.IsDisposed Then
            PictureBox1.Invalidate()
        End If
    End Sub

    Private Sub PushStateToRenderer()
        If isClosing OrElse _tileRenderer Is Nothing OrElse _model Is Nothing Then Return
        Dim stateData = _model.GetTimelineStateData()
        Dim hasSelection = (selectedFiles.Count > 0 AndAlso _model.TotalDuration > TimeSpan.Zero)
        Dim effHasAudio = _currentMediaInfo.HasAudio OrElse _isAudioReplaced

        _tileRenderer.UpdateState(stateData, _currentMediaInfo.Fps, hasSelection, _isAudioReplaced, effHasAudio)
        _tileRenderer.UpdateAudioOffset(_audioOffset, _bakedAudioOffset)
    End Sub

    Private Sub Renderer_PlayheadScrubbed(virtTime As TimeSpan)
        ' ИСПРАВЛЕНИЕ: Убрана команда StopPlayback(), которая вызывала черный экран при клике!
        If _playbackController IsNot Nothing AndAlso _playbackController.IsPlaying Then
            _wasPlayingBeforeScrub = True
            _playbackController.Pause()
            If _tileRendererRef IsNot Nothing Then _tileRendererRef.IsMediaPlaying = False
        End If

        _pendingSeekTime = virtTime
        _currentVirtualPlaybackTime = virtTime
        _tileRenderer.UpdatePlayhead(virtTime)
        UpdateDynamicStatus()
        ApplyDynamicVolume(virtTime)

        OnScrubPlayhead(virtTime)
    End Sub

    Private Sub OnScrubPlayhead(virtTime As TimeSpan)
        If isClosing OrElse String.IsNullOrEmpty(_currentFilePath) OrElse _directPlayer Is Nothing Then
            Return
        End If

        If InternalPreviewBox IsNot Nothing AndAlso InternalPreviewBox.Visible Then
            InternalPreviewBox.Visible = False
        End If

        _scrubTargetTime = virtTime

        If Not _isScrubLoopRunning Then
            StartScrubLoop()
        End If
    End Sub

    Private Sub StartScrubLoop()
        _isScrubLoopRunning = True
        _scrubLoopCts = New CancellationTokenSource()
        Dim token = _scrubLoopCts.Token

        Task.Run(Async Function() As Task
                     Try
                         Dim currentDecoderPath As String = String.Empty

                         Dim d3dDevice = _directPlayer?.GetDevice()
                         If d3dDevice Is Nothing Then Return

                         If _scrubFramePool Is Nothing OrElse _scrubFramePool.Width <> _currentMediaInfo.Width Then
                             Dim w = If(_currentMediaInfo.Width > 0, _currentMediaInfo.Width, 1920)
                             Dim h = If(_currentMediaInfo.Height > 0, _currentMediaInfo.Height, 1080)
                             _scrubFramePool = New GpuFramePool(d3dDevice, 3, w, h)
                         End If

                         While Not token.IsCancellationRequested
                             Dim targetVirt = _scrubTargetTime

                             If targetVirt <> _lastDecodedScrubTime AndAlso targetVirt >= TimeSpan.Zero Then
                                 Dim currentTarget = targetVirt
                                 Dim ctx = _model.GetVideoContextAtTime(currentTarget)

                                 If ctx IsNot Nothing Then
                                     Dim physicalTime = ctx.PhysicalTime
                                     Dim videoPath = ctx.Clip.FilePath

                                     If currentDecoderPath <> videoPath Then
                                         If _asyncDecoder IsNot Nothing Then _asyncDecoder.Dispose()
                                         _asyncDecoder = New AsyncMediaDecoder(videoPath)
                                         currentDecoderPath = videoPath
                                     End If

                                     ' Извлекаем кадр быстро (FastScrub) для плавности ползунка
                                     Dim gpuFrame = Await _asyncDecoder.ExtractFrameAsync(
                                         physicalTime, _scrubFramePool, d3dDevice.ImmediateContext, isFastScrub:=True, token)

                                     If Not token.IsCancellationRequested Then
                                         If gpuFrame IsNot Nothing Then
                                             ' Обязательно передаем выделенный клип для применения трансформаций (Zoom/Position)
                                             _directPlayer.ShowScrubFrame(gpuFrame, currentTarget, _tileRendererRef?.SelectedClip)
                                             gpuFrame.Dispose()
                                             _lastDecodedScrubTime = currentTarget
                                         End If
                                     Else
                                         gpuFrame?.Dispose()
                                     End If
                                 Else
                                     _lastDecodedScrubTime = currentTarget
                                 End If
                             Else
                                 Await Task.Delay(15, token) ' Экономим процессор, если время не менялось
                             End If
                         End While
                     Catch ex As OperationCanceledException
                     Catch ex As Exception
                         SafeLog("Ошибка ScrubLoop: " & ex.Message)
                     Finally
                         _isScrubLoopRunning = False
                     End Try
                 End Function)
    End Sub

    Private Sub Renderer_PlayheadSeekCompleted(virtTime As TimeSpan)
        ' Просто перенаправляем в единый метод завершения
        OnScrubCompleted(virtTime)
    End Sub

    Private Async Sub OnScrubCompleted(virtTime As TimeSpan)
        _scrubTargetTime = TimeSpan.MinValue
        If _scrubLoopCts IsNot Nothing Then
            _scrubLoopCts.Cancel()
            _scrubLoopCts.Dispose()
            _scrubLoopCts = Nothing
        End If

        _directPlayer?.EndScrubbing()

        If _playbackController IsNot Nothing Then
            _playbackController.Seek(virtTime)

            ' Если до перемотки видео играло - возобновляем
            If _wasPlayingBeforeScrub OrElse _wasPlayingBeforeSeek Then
                SafeCancelAndDisposeCTS(previewCts)
                SafeSetVideoViewPreviewImage(Nothing)

                _playbackController.ResumePlayback()
                StartPlaybackUIUpdateLoop()
                _awaitingFirstValidTime = False
                _wasPlayingBeforeScrub = False
                _wasPlayingBeforeSeek = False
                If _tileRendererRef IsNot Nothing Then _tileRendererRef.IsMediaPlaying = True
            Else
                ' ИСПРАВЛЕНИЕ: Если на паузе - извлекаем один точный высококачественный кадр!
                Await ForceHighQualityFrameAsync(virtTime)
            End If
        End If
    End Sub

    ' НОВЫЙ МЕТОД: Извлекает идеальный кадр при отпускании мышки
    Private Async Function ForceHighQualityFrameAsync(virtTime As TimeSpan) As Task
        If isClosing OrElse _directPlayer Is Nothing Then Return
        Dim d3dDevice = _directPlayer.GetDevice()
        If d3dDevice Is Nothing Then Return

        Dim ctx = _model.GetVideoContextAtTime(virtTime)
        If ctx IsNot Nothing Then
            Try
                If _asyncDecoder Is Nothing Then
                    _asyncDecoder = New AsyncMediaDecoder(ctx.Clip.FilePath)
                End If

                If _scrubFramePool Is Nothing Then
                    Dim w = If(_currentMediaInfo.Width > 0, _currentMediaInfo.Width, 1920)
                    Dim h = If(_currentMediaInfo.Height > 0, _currentMediaInfo.Height, 1080)
                    _scrubFramePool = New GpuFramePool(d3dDevice, 3, w, h)
                End If

                ' isFastScrub:=False заставляет FFmpeg найти точный кадр, а не ближайший
                Dim gpuFrame = Await _asyncDecoder.ExtractFrameAsync(ctx.PhysicalTime, _scrubFramePool, d3dDevice.ImmediateContext, isFastScrub:=False, CancellationToken.None)

                If gpuFrame IsNot Nothing Then
                    SafeSetVideoViewPreviewImage(Nothing) ' Скрываем заглушку
                    _directPlayer.ShowScrubFrame(gpuFrame, virtTime, _tileRendererRef?.SelectedClip)
                    gpuFrame.Dispose()
                End If
            Catch ex As Exception
                SafeLog("Ошибка точного кадра: " & ex.Message)
            End Try
        End If
    End Function

    Private Sub Renderer_MarkerStartChanged(newTime As TimeSpan)
        _model.SetMarkers(newTime, _model.MarkerEnd)
        UpdateMarkerTimeLabel()
        PushStateToRenderer()
    End Sub

    Private Sub Renderer_MarkerEndChanged(newTime As TimeSpan)
        _model.SetMarkers(_model.MarkerStart, newTime)
        UpdateMarkerTimeLabel()
        PushStateToRenderer()
    End Sub

    Private Sub Renderer_MarkersCommit()
        If selectedFiles.Count > 0 Then SaveMarkersForFile(selectedFiles(0))
    End Sub

    Private Sub Renderer_AudioOffsetChanged(offset As TimeSpan)
        _audioOffset = offset
        UpdateLabel(String.Format(CultureInfo.InvariantCulture, "Задержка звука: {0:F0} мс", _audioOffset.TotalMilliseconds))
        PushStateToRenderer()
    End Sub

    Private Sub Renderer_AudioOffsetCommit(offset As TimeSpan)
        _audioOffset = offset
        _playbackController?.SetAudioOffset(offset)
        UpdateLabel(String.Format(CultureInfo.InvariantCulture, "Синхронизация завершена. Смещение: {0:F0} мс", offset.TotalMilliseconds))
    End Sub

    Private Sub Renderer_PreviewRequested(virtTime As TimeSpan)
        ' ИСПРАВЛЕНИЕ: Отключаем конфликтный программный рендер при быстром аппаратном скраббинге таймлайна!
        If _isScrubLoopRunning Then Return

        pendingPreviewTime = virtTime
        previewTimer.Stop()
        previewTimer.Start()
    End Sub

    Private Sub Renderer_PlaybackPauseRequested()
        If _playbackController IsNot Nothing AndAlso _playbackController.State = IServices.PlaybackState.Playing Then
            _wasPlayingBeforeSeek = True
        End If

        _playbackController?.Pause()
        StopPlaybackUIUpdateLoop()

        If _tileRendererRef IsNot Nothing Then _tileRendererRef.IsMediaPlaying = False
        UpdateDynamicStatus(True)
    End Sub

    Private Sub Renderer_CursorMoved(virtTime As TimeSpan, mouseX As Integer)
        _hoverVirtualTime = virtTime
        UpdateDynamicStatus()
    End Sub

    Private Sub Renderer_CursorLeft()
        _hoverVirtualTime = Nothing
        UpdateDynamicStatus()
    End Sub

    Private Sub Renderer_TrackVolumeChanged(newVolume As Single)
        _trackVolume = newVolume
        SetVolumeFromTrackBar()
    End Sub

    Private Sub StartPopoutAnimation()
        popoutRotAngle = 0
        popoutFadeAlpha = 0
        _popoutStopwatch.Restart()
        PopoutAnimTimer.Start()
    End Sub

    Private Sub StopPopoutAnimation()
        PopoutAnimTimer.Stop()
        _popoutStopwatch.Stop()
    End Sub

    Private Sub PopoutAnimTimer_Tick(sender As Object, e As EventArgs) Handles PopoutAnimTimer.Tick
        If isClosing Then Return
        If PictureBox2 IsNot Nothing AndAlso Not PictureBox2.IsDisposed AndAlso PictureBox2.Visible Then
            PictureBox2.Invalidate()
        End If
    End Sub

    Private Function GetActiveAudioPath() As String
        If _isAudioBaked AndAlso Not String.IsNullOrEmpty(_currentBakedAudioPath) AndAlso IO.File.Exists(_currentBakedAudioPath) Then
            Return _currentBakedAudioPath
        ElseIf _isAudioReplaced AndAlso Not String.IsNullOrEmpty(_externalAudioPath) Then
            Return _externalAudioPath
        Else
            Return String.Empty
        End If
    End Function

    Private Sub SafeUIInvoke(action As Action, Optional onDropped As Action = Nothing)
        If Me.IsDisposed OrElse isClosing Then
            If onDropped IsNot Nothing Then onDropped()
            Return
        End If

        Try
            If Me.InvokeRequired Then
                Me.BeginInvoke(New Action(Sub()
                                              Try
                                                  If Not isClosing AndAlso Not Me.IsDisposed AndAlso Me.IsHandleCreated Then
                                                      action()
                                                  Else
                                                      If onDropped IsNot Nothing Then onDropped()
                                                  End If
                                              Catch ex As ObjectDisposedException
                                                  If onDropped IsNot Nothing Then onDropped()
                                              Catch ex As InvalidOperationException
                                                  If onDropped IsNot Nothing Then onDropped()
                                              Catch ex As Exception
                                                  Dim errStr As String = $"Критическая ошибка в UI-потоке (SafeUIInvoke):{vbCrLf}{ex}"
                                                  SafeLog(errStr)
                                                  MessageBox.Show($"Произошла непредвиденная ошибка в интерфейсе.{vbCrLf}{ex.Message}", "Ошибка интерфейса", MessageBoxButtons.OK, MessageBoxIcon.Error)
                                                  If onDropped IsNot Nothing Then onDropped()
                                              End Try
                                          End Sub))
            Else
                If Not isClosing AndAlso Not Me.IsDisposed Then
                    action()
                Else
                    If onDropped IsNot Nothing Then onDropped()
                End If
            End If
        Catch ex As ObjectDisposedException
            If onDropped IsNot Nothing Then onDropped()
        Catch ex As InvalidOperationException
            If onDropped IsNot Nothing Then onDropped()
        Catch ex As Exception
            Dim errStr As String = $"Ошибка вызова диспетчера SafeUIInvoke:{vbCrLf}{ex}"
            SafeLog(errStr)
            MessageBox.Show($"Не удалось выполнить команду интерфейса.{vbCrLf}{ex.Message}", "Системная ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error)
            If onDropped IsNot Nothing Then onDropped()
        End Try
    End Sub

    Private Sub VideoPanel1_MouseDoubleClick(sender As Object, e As MouseEventArgs) Handles VideoPanel1.MouseDoubleClick
        If e.Button = MouseButtons.Left Then
            If popoutForm?.IsDisposed = False Then
                popoutForm.ToggleFullscreen()
            Else
                TogglePopout()
            End If
        End If
    End Sub

    Private Async Sub VideoPanel1_MouseDown(sender As Object, e As MouseEventArgs) Handles VideoPanel1.MouseDown
        If (popoutForm IsNot Nothing AndAlso Not popoutForm.IsDisposed) OrElse
           (_playbackController IsNot Nothing AndAlso _playbackController.State = IServices.PlaybackState.Playing) Then
            Return
        End If

        If e.Button = MouseButtons.Right Then
            ClearCrop()
            Return
        End If

        If e.Button = MouseButtons.Left Then
            If _playbackController IsNot Nothing AndAlso _playbackController.State <> IServices.PlaybackState.Playing Then
                Dim targetTime As TimeSpan = _lastPreviewTime
                Await UpdatePreviewFrame(targetTime, True)

                IsCropModeActive = True
                _isDraggingCrop = True
                _startCropPoint = e.Location
                _currentCropRect = New System.Drawing.Rectangle(e.X, e.Y, 0, 0)
                If InternalPreviewBox IsNot Nothing AndAlso Not InternalPreviewBox.IsDisposed Then
                    InternalPreviewBox.Invalidate()
                End If
            End If
        End If
    End Sub

    Private Sub TogglePopout()
        If isClosing Then Return
        If popoutForm IsNot Nothing AndAlso Not popoutForm.IsDisposed Then Return
        If VideoPanel1 Is Nothing Then Return

        originalVideoViewBounds = VideoPanel1.Bounds
        originalVideoViewParent = VideoPanel1.Parent
        If originalVideoViewParent Is Nothing Then originalVideoViewParent = Me

        If PictureBox2 IsNot Nothing AndAlso Not PictureBox2.IsDisposed Then
            PictureBox2.Bounds = VideoPanel1.Bounds
            PictureBox2.Visible = True
            PictureBox2.BringToFront()
            StartPopoutAnimation()
        End If

        popoutForm = New Form2()
        AddHandler popoutForm.FormClosed, AddressOf PopoutForm_Closed

        popoutForm.CreateControl()
        popoutForm.Show()

        VideoPanel1.Parent = popoutForm
        VideoPanel1.Dock = DockStyle.Fill
        VideoPanel1.Visible = True
        VideoPanel1.BringToFront()

        Dim isPlaying As Boolean = _playbackController IsNot Nothing AndAlso _playbackController.IsPlaying
        If InternalPreviewBox IsNot Nothing AndAlso Not InternalPreviewBox.IsDisposed Then
            InternalPreviewBox.Visible = Not isPlaying
        End If

        ' ИСПРАВЛЕНИЕ: Блок изменения размера PictureBox1 (таймлайна) полностью удален.
        ' Таймлайн остается на своем месте и в своих размерах.

        Dim discardTask As Task = UpdatePreviewFrame(_lastPreviewTime, True).ContinueWith(Sub(t)
                                                                                              If t.IsFaulted Then
                                                                                                  SafeLog("Ошибка генерации превью (TogglePopout): " & t.Exception.GetBaseException().Message)
                                                                                              End If
                                                                                          End Sub)
    End Sub

    Private Sub PopoutForm_Closed(sender As Object, e As FormClosedEventArgs)
        If isClosing OrElse Me.IsDisposed Then Return

        If Me.InvokeRequired Then
            Try
                Me.BeginInvoke(New Action(Sub() PopoutForm_Closed(sender, e)))
            Catch ex As Exception
                SafeLog("Ошибка Invoke в PopoutForm_Closed: " & ex.Message)
            End Try
            Return
        End If

        If VideoPanel1 IsNot Nothing AndAlso Not VideoPanel1.IsDisposed Then
            Try
                VideoPanel1.Parent = If(originalVideoViewParent, Me)
                VideoPanel1.Dock = DockStyle.None
                VideoPanel1.Bounds = originalVideoViewBounds
                VideoPanel1.Anchor = AnchorStyles.Top Or AnchorStyles.Bottom Or AnchorStyles.Left Or AnchorStyles.Right
                VideoPanel1.Visible = True
                VideoPanel1.BringToFront()

                Dim isPlaying As Boolean = _playbackController IsNot Nothing AndAlso _playbackController.IsPlaying

                If InternalPreviewBox IsNot Nothing AndAlso Not InternalPreviewBox.IsDisposed Then
                    InternalPreviewBox.Visible = Not isPlaying
                End If

                If PictureBox2 IsNot Nothing AndAlso Not PictureBox2.IsDisposed Then
                    PictureBox2.Visible = False
                    StopPopoutAnimation()
                End If

            Catch ex As Exception
                SafeLog("Ошибка восстановления VideoPanel1: " & ex.Message)
            End Try
        End If

        ' ИСПРАВЛЕНИЕ: Блок изменения размера PictureBox1 (таймлайна) полностью удален.
        ' Таймлайн не будет "прыгать" при возврате плеера.

        If popoutForm IsNot Nothing Then
            If Not popoutForm.IsDisposed Then
                popoutForm.Dispose()
            End If
            popoutForm = Nothing
        End If

        Dim discardTask As Task = UpdatePreviewFrame(_lastPreviewTime, True).ContinueWith(Sub(t)
                                                                                              If t.IsFaulted Then
                                                                                                  SafeLog("Ошибка генерации превью (PopoutForm_Closed): " & t.Exception.GetBaseException().Message)
                                                                                              End If
                                                                                          End Sub)
    End Sub

    Private Sub PictureBox2_Paint(sender As Object, e As PaintEventArgs) Handles PictureBox2.Paint
        Dim pb As PictureBox = DirectCast(sender, PictureBox)
        Dim g As Graphics = e.Graphics
        g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias
        g.TextRenderingHint = Drawing.Text.TextRenderingHint.ClearTypeGridFit

        Dim bgColor As Color = If(_isDarkTheme, Color.FromArgb(255, 18, 18, 22), Color.FromArgb(255, 230, 230, 230))
        g.Clear(bgColor)

        Dim elapsed As Double = _popoutStopwatch.Elapsed.TotalSeconds
        Dim pulse As Double = (Math.Sin(elapsed * Math.PI * 2 / 3.0) + 1.0) / 2.0
        Dim alpha As Integer = CInt(70 + pulse * 130)

        Dim cx As Single = pb.Width / 2.0F
        Dim cy As Single = pb.Height / 2.0F

        Dim penColor As Color = If(_isDarkTheme, Color.FromArgb(alpha, 100, 100, 110), Color.FromArgb(alpha, 150, 150, 150))
        Using pen As New Pen(penColor, 1.5F)
            pen.DashStyle = Drawing2D.DashStyle.Dash
            Dim rectW As Single = 300.0F
            Dim rectH As Single = 160.0F
            g.DrawRectangle(pen, cx - rectW / 2, cy - rectH / 2, rectW, rectH)
        End Using

        Using fontTitle As New Font("Segoe UI Semibold", 12.0F)
            Dim textColor As Color = Color.FromArgb(alpha, ThemeManager.ForeColor)
            Using brushTitle As New SolidBrush(textColor)
                Dim format As New StringFormat() With {.Alignment = StringAlignment.Center, .LineAlignment = StringAlignment.Far}
                g.DrawString("Плеер откреплен", fontTitle, brushTitle, New RectangleF(0, 0, pb.Width, cy - 20), format)
            End Using
        End Using

        DrawVideoStatsOnPopout(g, cx, cy, alpha)
    End Sub

    Private Sub DrawVideoStatsOnPopout(g As Graphics, cx As Single, cy As Single, textAlpha As Integer)
        If _currentMediaInfo.Width = 0 Then Return

        Dim currentSec As Double = 0
        If _playbackController IsNot Nothing Then
            currentSec = _playbackController.CurrentVirtualTime.TotalSeconds
        End If
        Dim currentFrame As Integer = CInt(Math.Floor(currentSec * _currentMediaInfo.Fps))

        Dim resStr As String = $"{_currentMediaInfo.Width} x {_currentMediaInfo.Height}"
        Dim fpsStr As String = $"{_currentMediaInfo.Fps.ToString("F2", CultureInfo.InvariantCulture)} FPS"
        Dim vCodec As String = _currentMediaInfo.Codec.ToUpper()
        Dim aCodec As String = If(_currentMediaInfo.HasAudio OrElse _isAudioReplaced, "AUDIO: YES", "AUDIO: NONE")

        Dim statsText As String = $"Кадр: {currentFrame}" & vbCrLf &
                                  $"Разрешение: {resStr}" & vbCrLf &
                                  $"Частота: {fpsStr}" & vbCrLf &
                                  $"Поток: {vCodec} | {aCodec}"

        Using fontStats As New Font("Consolas", 10.0F)
            Dim statsColor As Color = If(_isDarkTheme, Color.FromArgb(Math.Min(255, textAlpha + 50), 160, 160, 170), Color.FromArgb(Math.Min(255, textAlpha + 50), 80, 80, 90))
            Using brushStats As New SolidBrush(statsColor)
                Dim format As New StringFormat() With {.Alignment = StringAlignment.Center, .LineAlignment = StringAlignment.Near}
                g.DrawString(statsText, fontStats, brushStats, New RectangleF(0, cy, PictureBox2.Width, PictureBox2.Height - cy), format)
            End Using
        End Using
    End Sub

    Private Sub StartProLoading()
        SafeUIInvoke(Sub()
                         SyncLock _proLoadingLock
                             Dim newCount As Integer = Interlocked.Increment(loadingCount)
                             If newCount > 1 Then Return

                             rotAngle = 0
                             fadeAlpha = 0
                             isProLoading = True

                             _loadingStopwatch.Restart()
                             LoadingTimer.Start()

                             _tileRenderer?.UpdateLoadingState(isProLoading, rotAngle, fadeAlpha)
                         End SyncLock
                     End Sub)
    End Sub

    Private Sub StopProLoading()
        SafeUIInvoke(Sub()
                         SyncLock _proLoadingLock
                             Dim newCount As Integer = Interlocked.Decrement(loadingCount)
                             If newCount > 0 Then Return
                             If newCount < 0 Then
                                 Interlocked.Exchange(loadingCount, 0)
                             End If

                             LoadingTimer.Stop()
                             _loadingStopwatch.Stop()
                             isProLoading = False

                             _tileRenderer?.UpdateLoadingState(isProLoading, rotAngle, fadeAlpha)
                         End SyncLock
                     End Sub)
    End Sub

    Private Sub LoadingTimer_Tick(sender As Object, e As EventArgs) Handles LoadingTimer.Tick
        If isClosing OrElse PictureBox1 Is Nothing OrElse PictureBox1.Width < 10 OrElse PictureBox1.Height < 10 Then Return

        Dim elapsed As Double = _loadingStopwatch.Elapsed.TotalSeconds
        rotAngle = CSng((elapsed * 420.0) Mod 360.0)

        Dim fadeDuration As Double = 0.4
        Dim progress As Double = Math.Min(1.0, elapsed / fadeDuration)
        Dim easedProgress As Double = progress * (2.0 - progress)

        fadeAlpha = CSng(easedProgress * 255.0)

        _tileRenderer?.UpdateLoadingState(isProLoading, rotAngle, fadeAlpha)
    End Sub

    Private Function FormatTimeSpan(ts As TimeSpan) As String
        Return FFmpegCommandBuilder.FormatTimeForFFmpeg(ts)
    End Function

    Private Sub UpdateDynamicStatus(Optional forceUpdate As Boolean = False)
        Static statusLock As New Object()
        SyncLock statusLock
            If isClosing Then Return
            If Not forceUpdate AndAlso (DateTime.Now - lastStatusUpdateTime).TotalMilliseconds < 50 Then Return
            lastStatusUpdateTime = DateTime.Now
        End SyncLock

        SafeUIInvoke(Sub()
                         If ToolStripLabel2?.IsDisposed = False Then
                             If selectedFiles Is Nothing OrElse selectedFiles.Count = 0 Then
                                 ToolStripLabel2.Text = "Время: 00:00:00.000"
                             Else
                                 ToolStripLabel2.Text = FormatTimeSpan(_model.TotalDuration)
                             End If
                         End If

                         If Label6?.IsDisposed = False Then
                             If selectedFiles Is Nothing OrElse selectedFiles.Count = 0 Then
                                 Label6.Text = "Начало: 00:00:00.000 | Конец: 00:00:00.000 | Длительность: 00:00:00.000"
                                 If Label2?.IsDisposed = False Then Label2.Text = "Готов к работе"
                                 Return
                             End If

                             Dim finalDurationSec As Double = CalculateTargetDuration(_model.Cuts.Count > 0)
                             Dim finalDuration As TimeSpan = TimeSpan.FromSeconds(finalDurationSec)

                             Dim formattedStart As String = FormatTimeSpan(_model.MarkerStart)
                             Dim formattedEnd As String = FormatTimeSpan(_model.MarkerEnd)
                             Dim formattedFinal As String = FormatTimeSpan(finalDuration)

                             Dim baseLabel6 As String = $"Начало: {formattedStart} | Конец: {formattedEnd} | Длительность: {formattedFinal}"

                             Dim isPlayerPlaying As Boolean = _playbackController IsNot Nothing AndAlso _playbackController.State = IServices.PlaybackState.Playing
                             Dim currentTime As TimeSpan = If(_playbackController IsNot Nothing, _playbackController.CurrentVirtualTime, TimeSpan.Zero)

                             If _tileRendererRef IsNot Nothing Then
                                 _tileRendererRef.IsMediaPlaying = isPlayerPlaying
                             End If

                             Dim currentEngine As String = _currentAudioEngineName
                             If Not _currentMediaInfo.HasAudio AndAlso Not _isAudioReplaced Then
                                 currentEngine = "Без звука"
                             End If

                             If isPlayerPlaying Then
                                 Label6.Text = baseLabel6
                                 If Label2?.IsDisposed = False Then
                                     Label2.Text = $"Воспроизведение [{currentEngine}]: {FormatTimeSpan(currentTime)}"
                                 End If
                             ElseIf _playbackController IsNot Nothing AndAlso _playbackController.State = IServices.PlaybackState.Paused AndAlso currentTime > TimeSpan.Zero Then
                                 Label6.Text = baseLabel6
                                 If Label2?.IsDisposed = False Then
                                     Label2.Text = $"Пауза [{currentEngine}]: {FormatTimeSpan(currentTime)}"
                                 End If
                             ElseIf _hoverVirtualTime.HasValue Then
                                 Label6.Text = $"{baseLabel6} | Курсор: {FormatTimeSpan(_hoverVirtualTime.Value)}"
                                 If Label2?.IsDisposed = False AndAlso Label2.Text.StartsWith("Воспроизведение") Then
                                     Label2.Text = "Воспроизведение остановлено"
                                 End If
                             Else
                                 Label6.Text = baseLabel6
                             End If
                         End If
                     End Sub)
    End Sub

    Private Sub Form1_Resize(sender As Object, e As EventArgs) Handles MyBase.Resize
        If Me.WindowState = FormWindowState.Minimized Then Return

        ' Мы больше не вмешиваемся в размеры PictureBox1 вручную!

        ' Сообщаем рендеру DirectX новый размер, если форма была растянута пользователем 
        ' (и если у PictureBox1 настроены свойства Anchor в конструкторе)
        If _tileRenderer IsNot Nothing AndAlso PictureBox1 IsNot Nothing AndAlso PictureBox1.Width > 0 AndAlso PictureBox1.Height > 0 Then
            _tileRenderer.Resize(PictureBox1.ClientSize.Width, PictureBox1.ClientSize.Height)
        End If

        If _playbackController IsNot Nothing AndAlso Not _playbackController.IsPlaying Then
            PushStateToRenderer()
        End If
    End Sub

    Private Sub UpdateTimelineSize()
        If PictureBox1 IsNot Nothing AndAlso Not PictureBox1.IsDisposed Then
            Me.SuspendLayout()
            Try
                Dim targetHeight As Integer = CInt(Me.ClientSize.Height * 0.08F)
                If targetHeight < 100 Then targetHeight = 100

                PictureBox1.Height = targetHeight
                PictureBox1.Width = Me.ClientSize.Width
                PictureBox1.Left = 15
                PictureBox1.Top = Me.ClientSize.Height - targetHeight

                PushStateToRenderer()
                PictureBox1.Invalidate()
            Finally
                Me.ResumeLayout()
            End Try
        End If
    End Sub

    Protected Overrides Function ProcessCmdKey(ByRef msg As Message, keyData As Keys) As Boolean
        If keyData = (Keys.Control Or Keys.Z) Then
            If _model IsNot Nothing Then
                _model.Undo()
                If PictureBox1 IsNot Nothing AndAlso Not PictureBox1.IsDisposed Then
                    PushStateToRenderer()
                    PictureBox1.Invalidate()
                End If
                UpdateLabel("Отмена (Undo)")
            End If
            Return True
        End If

        If keyData = (Keys.Control Or Keys.Y) OrElse keyData = (Keys.Control Or Keys.Shift Or Keys.Z) Then
            If _model IsNot Nothing Then
                _model.Redo()
                If PictureBox1 IsNot Nothing AndAlso Not PictureBox1.IsDisposed Then
                    PushStateToRenderer()
                    PictureBox1.Invalidate()
                End If
                UpdateLabel("Повтор (Redo)")
            End If
            Return True
        End If

        If keyData = Keys.S Then
            If _model IsNot Nothing AndAlso _playbackController IsNot Nothing AndAlso selectedFiles.Count > 0 Then
                Dim splitTime As TimeSpan = _playbackController.CurrentVirtualTime
                _model.SplitClipAtTime(splitTime)

                If PictureBox1 IsNot Nothing AndAlso Not PictureBox1.IsDisposed Then
                    PushStateToRenderer()
                    PictureBox1.Invalidate()
                End If
                UpdateLabel("Клип разрезан.")
            End If
            Return True
        End If

        If keyData = Keys.Delete Then
            If _model IsNot Nothing AndAlso _tileRendererRef IsNot Nothing Then
                Dim clipToDelete = _tileRendererRef.SelectedClip
                If clipToDelete IsNot Nothing Then
                    _model.RemoveClip(clipToDelete.Id)
                    _tileRendererRef.SelectedClip = Nothing

                    If PictureBox1 IsNot Nothing AndAlso Not PictureBox1.IsDisposed Then
                        PushStateToRenderer()
                        PictureBox1.Invalidate()
                    End If
                    UpdateLabel("Клип удален.")
                End If
            End If
            Return True
        End If

        Return MyBase.ProcessCmdKey(msg, keyData)
    End Function

    Private Sub Form1_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        Try
            Dim logDir As String = IO.Path.Combine(Application.StartupPath, "logs")
            If Not IO.Directory.Exists(logDir) Then IO.Directory.CreateDirectory(logDir)

            Log.Logger = New LoggerConfiguration() _
                .MinimumLevel.Debug() _
                .WriteTo.Async(Sub(a) a.File(IO.Path.Combine(logDir, "log.txt"), rollingInterval:=RollingInterval.Day)) _
                .CreateLogger()
        Catch ex As Exception
            System.Diagnostics.Debug.WriteLine("Ошибка инициализации Serilog: " & ex.Message)
        End Try

        AddHandler Application.ThreadException, Sub(s, args)
                                                    SafeLog("Критическая ошибка (UI Thread): " & args.Exception.Message)
                                                End Sub
        AddHandler AppDomain.CurrentDomain.UnhandledException, Sub(s, args)
                                                                   Dim exObj = TryCast(args.ExceptionObject, Exception)
                                                                   If exObj IsNot Nothing Then
                                                                       SafeLog("Критическая ошибка (AppDomain): " & exObj.Message)
                                                                   End If
                                                               End Sub

        _model = New ProjectModel()
        Dim tileSize As New Size(80, 130)

        _tileRenderer = New TileTimelineRenderer(tileSize) With {
            .IsDarkTheme = _isDarkTheme
        }

        _tileRendererRef = DirectCast(_tileRenderer, TileTimelineRenderer)
        _tileRendererRef.ProjectModel = _model

        ' ==============================================================
        ' НОВОЕ: Считываем TimelineThumbMode из файла настроек
        ' и передаем его в рендерер при загрузке формы.
        ' ==============================================================
        If _tileRendererRef IsNot Nothing Then
            _tileRendererRef.TimelineThumbMode = SettingsService.Instance.Current.TimelineThumbMode
        End If

        AddHandler _tileRendererRef.TrackVolumeChanged, AddressOf Renderer_TrackVolumeChanged
        _trackVolume = _tileRendererRef.TrackVolume

        _presenter = New MainEditorPresenter(Me, _model, _tileRenderer, _videoPlayer, _ffmpegService, _fileManager)

        If _fileManager IsNot Nothing Then
            AddHandler _fileManager.LogMessage, Sub(msg) SafeLog(msg)
        End If

        Try
            Dim tempPath As String = IO.Path.GetTempPath()
            Dim oldCacheFiles As String() = IO.Directory.GetFiles(tempPath, "frame_cache_*.ybin")
            For Each file In oldCacheFiles
                Try
                    IO.File.Delete(file)
                    SafeLog($"Удален старый кэш-файл MMF: {file}")
                Catch ex As Exception
                End Try
            Next
        Catch ex As Exception
            SafeLog($"Ошибка при очистке временных файлов MMF: {ex.Message}")
        End Try

        If PictureBox1 IsNot Nothing Then
            ' ИСПРАВЛЕНИЕ: Мы ничего не меняем в размерах и привязках PictureBox1.
            ' Оставляем всё так, как вы настроили в визуальном конструкторе Visual Studio.
            SafeLog($"Инициализация PictureBox1: Размер {PictureBox1.Width}x{PictureBox1.Height}")

            Try
                Dim prop = GetType(Control).GetProperty("DoubleBuffered", Reflection.BindingFlags.NonPublic Or Reflection.BindingFlags.Instance)
                If prop IsNot Nothing Then
                    prop.SetValue(PictureBox1, False)
                    SafeLog("Двойная буферизация PictureBox1 отключена")
                End If

                Dim methodSetStyle = GetType(Control).GetMethod("SetStyle", Reflection.BindingFlags.NonPublic Or Reflection.BindingFlags.Instance)
                If methodSetStyle IsNot Nothing Then
                    methodSetStyle.Invoke(PictureBox1, New Object() {ControlStyles.Opaque, True})
                    SafeLog("Стиль Opaque для PictureBox1 включен")
                End If
            Catch ex As Exception
                SafeLog($"Ошибка при отключении буферизации PictureBox1: {ex.Message}")
            End Try
        End If
        PictureBox1.AllowDrop = True

        Try
            isClosing = False
            Me.PerformLayout()
            PictureBox1?.CreateControl()

            If _ffmpegService IsNot Nothing Then
                AddHandler _ffmpegService.LogMessage, Sub(msg) SafeLog(msg)
            End If

            If Label2 IsNot Nothing Then Label2.Text = "Инициализация..."

            InitializeUI()

            _tileRenderer.Initialize(PictureBox1)

            AddHandler _tileRenderer.PlayheadScrubbed, AddressOf OnScrubPlayhead
            AddHandler _tileRenderer.PlayheadSeekCompleted, AddressOf OnScrubCompleted
            AddHandler _tileRenderer.MarkerStartChanged, AddressOf Renderer_MarkerStartChanged
            AddHandler _tileRenderer.MarkerEndChanged, AddressOf Renderer_MarkerEndChanged
            AddHandler _tileRenderer.MarkersCommit, AddressOf Renderer_MarkersCommit
            AddHandler _tileRenderer.AudioOffsetChanged, AddressOf Renderer_AudioOffsetChanged
            AddHandler _tileRenderer.AudioOffsetCommit, AddressOf Renderer_AudioOffsetCommit
            AddHandler _tileRenderer.PreviewRequested, AddressOf Renderer_PreviewRequested
            AddHandler _tileRenderer.PlaybackPauseRequested, AddressOf Renderer_PlaybackPauseRequested
            AddHandler _tileRenderer.CursorMoved, AddressOf Renderer_CursorMoved
            AddHandler _tileRenderer.CursorLeft, AddressOf Renderer_CursorLeft
            AddHandler _tileRendererRef.SelectionChanged, AddressOf Renderer_SelectionChanged

            If PictureBox1 IsNot Nothing Then
                _tileRenderer.Resize(PictureBox1.ClientSize.Width, PictureBox1.ClientSize.Height)
            End If
            SafeLog("Рендерер инициализирован при старте")

            If VideoPanel1 IsNot Nothing Then
                VideoPanel1.CreateControl()
                VideoPanel1.BackColor = Color.Black
                VideoPanel1.Anchor = AnchorStyles.Top Or AnchorStyles.Bottom Or AnchorStyles.Left Or AnchorStyles.Right
                VideoPanel1.Visible = True

                InternalPreviewBox = New PictureBox With {
                    .Name = "InternalPreviewBox",
                    .Dock = DockStyle.Fill,
                    .BackColor = Color.Black,
                    .SizeMode = PictureBoxSizeMode.Zoom,
                    .Visible = False
                }

                AddHandler InternalPreviewBox.MouseDoubleClick, Sub(sBox, ea) VideoPanel1_MouseDoubleClick(VideoPanel1, ea)
                AddHandler InternalPreviewBox.MouseDown, AddressOf InternalPreviewBox_MouseDown
                AddHandler InternalPreviewBox.MouseMove, AddressOf InternalPreviewBox_MouseMove
                AddHandler InternalPreviewBox.MouseUp, AddressOf InternalPreviewBox_MouseUp
                AddHandler InternalPreviewBox.Paint, AddressOf InternalPreviewBox_Paint

                VideoPanel1.Controls.Add(InternalPreviewBox)
            End If

            If PictureBox2 IsNot Nothing Then
                PictureBox2.Parent = If(VideoPanel1 IsNot Nothing AndAlso VideoPanel1.Parent IsNot Nothing, VideoPanel1.Parent, Me)
                If VideoPanel1 IsNot Nothing Then
                    PictureBox2.Bounds = VideoPanel1.Bounds
                    PictureBox2.Anchor = VideoPanel1.Anchor
                End If
                PictureBox2.BackColor = Color.Black
                PictureBox2.SizeMode = PictureBoxSizeMode.CenterImage
                PictureBox2.Visible = False
            End If

            Dim audioEngine As String = SettingsService.Instance.Current.AudioEngine

            Try
                If audioEngine = "WASAPI" Then
                    _audioPlayer = New NAudioSyncPlayer(NAudioSyncPlayer.NAudioBackend.Wasapi)
                    _currentAudioEngineName = "WASAPI"
                    SafeLog("Загружен аудиодвижок: WASAPI")
                ElseIf audioEngine = "ASIO" Then
                    _audioPlayer = New NAudioSyncPlayer(NAudioSyncPlayer.NAudioBackend.Asio)
                    _currentAudioEngineName = "ASIO"
                    SafeLog("Загружен аудиодвижок: ASIO")
                Else
                    _audioPlayer = New NAudioSyncPlayer(NAudioSyncPlayer.NAudioBackend.Wasapi)
                    _currentAudioEngineName = "WASAPI (fallback)"
                    SafeLog("Загружен аудиодвижок: WASAPI (fallback)")
                End If
            Catch ex As Exception
                SafeLog($"Ошибка инициализации {audioEngine}, откат к WASAPI: {ex.Message}")
                _audioPlayer = New NAudioSyncPlayer(NAudioSyncPlayer.NAudioBackend.Wasapi)
                _currentAudioEngineName = "WASAPI (fallback error)"
            End Try

            _playbackController = New yoump.PlaybackController(_videoPlayer, _audioPlayer, _model)

            AddHandler _playbackController.TimeChanged, AddressOf OnPlaybackTimeChanged
            AddHandler _playbackController.PlaybackStopped, AddressOf OnPlaybackStopped
            AddHandler _playbackController.PlaybackError, AddressOf OnPlaybackError
            AddHandler _playbackController.MarkerReached, AddressOf OnMarkerReached

            If previewTimer IsNot Nothing Then previewTimer.Interval = 80
            If resizeDebounceTimer IsNot Nothing Then resizeDebounceTimer.Interval = 300
            If playbackTimer Is Nothing Then playbackTimer = New System.Windows.Forms.Timer() With {.Interval = 60}

            Try
                If _videoPlayer IsNot Nothing Then
                    _videoPlayer.Initialize(Application.StartupPath, VideoPanel1)
                    AddHandler _videoPlayer.LogMessage, Sub(msg) SafeLog(msg)

                    _directPlayer = TryCast(_videoPlayer, Direct3D11VideoPlayer)
                End If
                InitializeVolumeUI()
            Catch ex As Exception
                SafeLog("Не удалось инициализировать видеоплеер: " & ex.Message)
                MessageBox.Show("Не удалось инициализировать видеоплеер: " & ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error)
            End Try

            If Not Me.DesignMode AndAlso System.ComponentModel.LicenseManager.UsageMode <> System.ComponentModel.LicenseUsageMode.Designtime Then
                Dim bgTask As Task = Task.Run(AddressOf LoadBackgroundDataAsync)
            Else
                If Label2 IsNot Nothing Then Label2.Text = "Дизайнер"
            End If

            UpdateDynamicStatus()

            If Not isClosing AndAlso PictureBox1 IsNot Nothing AndAlso Not PictureBox1.IsDisposed Then
                PushStateToRenderer()
                PictureBox1.Visible = True
                PictureBox1.BringToFront()
            End If

            AddHandler ThemeManager.ThemeChanged, AddressOf OnGlobalThemeChanged

        Catch ex As Exception
            SafeLog("Ошибка загрузки (Form1_Load): " & ex.Message)
            MessageBox.Show("Ошибка инициализации формы: " & ex.Message, "Критическая ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

    Private Sub PictureBox1_DragEnter(sender As Object, e As DragEventArgs) Handles PictureBox1.DragEnter
        If e.Data.GetDataPresent(DataFormats.FileDrop) Then
            e.Effect = DragDropEffects.Copy
        Else
            e.Effect = DragDropEffects.None
        End If
    End Sub

    Private Async Sub PictureBox1_DragDrop(sender As Object, e As DragEventArgs) Handles PictureBox1.DragDrop
        If isClosing Then Return

        Try
            If e.Data.GetDataPresent(DataFormats.FileDrop) Then
                Dim droppedFiles As String() = CType(e.Data.GetData(DataFormats.FileDrop), String())
                Dim resolvedFiles As New List(Of String)()

                For Each file In droppedFiles
                    Dim actualFilePath As String = ResolveBdmvToM2ts(file)
                    If Not String.IsNullOrEmpty(actualFilePath) Then
                        resolvedFiles.Add(actualFilePath)
                    End If
                Next

                If resolvedFiles.Count > 0 Then
                    Dim append As Boolean = (selectedFiles.Count > 0)
                    Await ImportMediaFilesAsync(resolvedFiles, append)
                End If
            End If
        Catch ex As Exception
            SafeLog("Ошибка при Drag&Drop импорте: " & ex.Message)
            MessageBox.Show("Не удалось импортировать файлы: " & ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

    Private Sub OnPlaybackTimeChanged(sender As Object, virtualTime As TimeSpan)
        If isClosing Then Return

        If _awaitingFirstValidTime Then
            If virtualTime.TotalMilliseconds <= 50 Then Return
            _awaitingFirstValidTime = False
        End If

        If _pendingSeekTime > TimeSpan.Zero Then
            If Math.Abs((virtualTime - _pendingSeekTime).TotalSeconds) > 1.0 Then
                Return
            Else
                _pendingSeekTime = TimeSpan.Zero
            End If
        End If

        _currentVirtualPlaybackTime = virtualTime
        _tileRenderer?.UpdatePlayhead(virtualTime)
        UpdateDynamicStatus()

        ApplyDynamicVolume(virtualTime)
        ApplyDynamicVideoFade(virtualTime)
    End Sub

    Private Sub ApplyDynamicVideoFade(currentVirtTime As TimeSpan)
        If _videoPlayer Is Nothing OrElse _tileRendererRef Is Nothing OrElse _model Is Nothing Then Return

        Dim renderer As TileTimelineRenderer = TryCast(_tileRendererRef, TileTimelineRenderer)
        If renderer Is Nothing Then Return

        Dim markerStart As TimeSpan = _model.MarkerStart
        Dim markerEnd As TimeSpan = _model.MarkerEnd

        Dim vFadeIn As TimeSpan = renderer.VideoFadeIn
        Dim vFadeOut As TimeSpan = renderer.VideoFadeOut
        Dim vFadeInType As Integer = CInt(renderer.VideoFadeInType)
        Dim vFadeOutType As Integer = CInt(renderer.VideoFadeOutType)

        Dim targetOpacity As Single = 1.0F

        Dim isPlaying As Boolean = False
        If _playbackController IsNot Nothing Then
            isPlaying = (_playbackController.State = IServices.PlaybackState.Playing)
        End If

        If isPlaying Then
            If vFadeIn > TimeSpan.Zero AndAlso currentVirtTime >= markerStart AndAlso currentVirtTime <= (markerStart + vFadeIn) Then
                Dim progress As Single = CSng((currentVirtTime - markerStart).TotalSeconds / vFadeIn.TotalSeconds)
                targetOpacity = GetCurveMultiplier(progress, vFadeInType)
            End If

            If vFadeOut > TimeSpan.Zero AndAlso currentVirtTime <= markerEnd AndAlso currentVirtTime >= (markerEnd - vFadeOut) Then
                Dim fadeStart As TimeSpan = markerEnd - vFadeOut
                Dim progress As Single = 1.0F - CSng((currentVirtTime - fadeStart).TotalSeconds / vFadeOut.TotalSeconds)
                targetOpacity = GetCurveMultiplier(progress, vFadeOutType)
            End If
        End If

        Dim currentSec As Double = _fadeStopwatch.Elapsed.TotalSeconds
        Dim deltaTime As Double = currentSec - _lastFadeTickSec
        _lastFadeTickSec = currentSec

        If deltaTime > 0.1 Then deltaTime = 0.1

        Dim lerpFactor As Single = If(isPlaying, 1.0F - CSng(Math.Exp(-12.0 * deltaTime)), 1.0F)

        _smoothedOpacity += (targetOpacity - _smoothedOpacity) * lerpFactor

        _videoPlayer.SetVideoOpacity(_smoothedOpacity)
    End Sub

    Private Function GetCurveMultiplier(progress As Single, curveType As Integer) As Single
        If progress <= 0.0F Then Return 0.0F
        If progress >= 1.0F Then Return 1.0F

        Select Case curveType
            Case 1
                Return CSng(Math.Sqrt(progress))
            Case 2
                Return progress * progress
            Case 3
                Return progress * progress * (3.0F - 2.0F * progress)
            Case 4
                If progress < 0.5F Then
                    Return 2.0F * progress * progress
                Else
                    Return 1.0F - 2.0F * (1.0F - progress) * (1.0F - progress)
                End If
            Case Else
                Return progress
        End Select
    End Function

    Private Sub OnPlaybackStopped(sender As Object, e As EventArgs)
        SafeUIInvoke(Async Sub()
                         StopPlaybackUIUpdateLoop()
                         _awaitingFirstValidTime = False
                         If _tileRendererRef IsNot Nothing Then _tileRendererRef.IsMediaPlaying = False
                         UpdateLabel("Воспроизведение остановлено")
                         UpdateDynamicStatus(True)

                         Await Task.Delay(50)

                         Dim discardTask As Task = TriggerPreviewNowAsync(_currentVirtualPlaybackTime)
                     End Sub)
    End Sub

    Private Sub OnPlaybackError(sender As Object, message As String)
        SafeLog("Ошибка воспроизведения: " & message)
        MessageBox.Show(message, "Ошибка воспроизведения", MessageBoxButtons.OK, MessageBoxIcon.Error)
    End Sub

    Private Sub OnMarkerReached(sender As Object, e As EventArgs)
        StopPlaybackUIUpdateLoop()
        _awaitingFirstValidTime = False

        If _tileRendererRef IsNot Nothing Then _tileRendererRef.IsMediaPlaying = False

        UpdateLabel("Воспроизведение завершено (достигнут маркер)")
        UpdateDynamicStatus(True)

        Dim discardTask As Task = TriggerPreviewNowAsync(_currentVirtualPlaybackTime)
    End Sub

    Private Sub PlaybackTimer_Tick(sender As Object, e As EventArgs) Handles playbackTimer.Tick
        If isClosing OrElse _playbackController Is Nothing Then Return
        _playbackController.ProcessTick()
    End Sub

    Private Sub InternalPreviewBox_MouseDown(sender As Object, e As MouseEventArgs)
        If (popoutForm IsNot Nothing AndAlso Not popoutForm.IsDisposed) OrElse
           (_playbackController IsNot Nothing AndAlso _playbackController.State = IServices.PlaybackState.Playing) Then
            Return
        End If

        If e.Button = MouseButtons.Right Then
            ClearCrop()
            Return
        End If

        If e.Button = MouseButtons.Left Then
            IsCropModeActive = True
            _isDraggingCrop = True
            _startCropPoint = e.Location
            _currentCropRect = New System.Drawing.Rectangle(e.X, e.Y, 0, 0)
            InternalPreviewBox.Invalidate()
        End If
    End Sub

    Private Sub InternalPreviewBox_MouseMove(sender As Object, e As MouseEventArgs)
        If (popoutForm IsNot Nothing AndAlso Not popoutForm.IsDisposed) OrElse
           (_playbackController IsNot Nothing AndAlso _playbackController.State = IServices.PlaybackState.Playing) Then
            Return
        End If

        If IsCropModeActive AndAlso _isDraggingCrop Then
            Dim x As Integer = Math.Min(_startCropPoint.X, e.X)
            Dim y As Integer = Math.Min(_startCropPoint.Y, e.Y)
            Dim w As Integer = Math.Abs(e.X - _startCropPoint.X)
            Dim h As Integer = Math.Abs(e.Y - _startCropPoint.Y)

            If x < 0 Then
                w += x
                x = 0
            End If
            If y < 0 Then
                h += y
                y = 0
            End If

            If x + w > InternalPreviewBox.Width Then w = InternalPreviewBox.Width - x
            If y + h > InternalPreviewBox.Height Then h = InternalPreviewBox.Height - y

            _currentCropRect = New System.Drawing.Rectangle(x, y, w, h)
            InternalPreviewBox.Invalidate()
        End If
    End Sub

    Private Sub InternalPreviewBox_MouseUp(sender As Object, e As MouseEventArgs)
        If (popoutForm IsNot Nothing AndAlso Not popoutForm.IsDisposed) OrElse
           (_playbackController IsNot Nothing AndAlso _playbackController.State = IServices.PlaybackState.Playing) Then
            Return
        End If

        If IsCropModeActive AndAlso e.Button = MouseButtons.Left AndAlso _isDraggingCrop Then
            _isDraggingCrop = False
            CalculateRealCropCoordinates()
            InternalPreviewBox.Invalidate()
        End If
    End Sub

    Private Sub InternalPreviewBox_Paint(sender As Object, e As PaintEventArgs)
        If IsCropModeActive AndAlso _currentCropRect.Width > 0 AndAlso _currentCropRect.Height > 0 Then
            Dim g As Graphics = e.Graphics
            Dim pbRect As System.Drawing.Rectangle = InternalPreviewBox.ClientRectangle

            Using dimBrush As New SolidBrush(ThemeManager.DimOverlayColor)
                g.FillRectangle(dimBrush, 0, 0, pbRect.Width, _currentCropRect.Top)
                g.FillRectangle(dimBrush, 0, _currentCropRect.Bottom, pbRect.Width, pbRect.Height - _currentCropRect.Bottom)
                g.FillRectangle(dimBrush, 0, _currentCropRect.Top, _currentCropRect.Left, _currentCropRect.Height)
                g.FillRectangle(dimBrush, _currentCropRect.Right, _currentCropRect.Top, pbRect.Width - _currentCropRect.Right, _currentCropRect.Height)
            End Using

            Using pen As New Pen(Color.LimeGreen, 2)
                pen.DashStyle = Drawing2D.DashStyle.Dash
                g.DrawRectangle(pen, _currentCropRect)
            End Using

            If FinalCropW > 0 AndAlso FinalCropH > 0 Then
                Using font As New Font("Segoe UI", 10, FontStyle.Bold)
                    Using textBrush As New SolidBrush(ThemeManager.ForeColor)
                        Using bgBrush As New SolidBrush(ThemeManager.DimOverlayColor)
                            Dim text As String = $"{FinalCropW} x {FinalCropH}"
                            Dim textSize As SizeF = g.MeasureString(text, font)
                            Dim textRect As New RectangleF(_currentCropRect.X, _currentCropRect.Y - textSize.Height - 4, textSize.Width + 4, textSize.Height + 4)
                            If textRect.Y < 0 Then textRect.Y = _currentCropRect.Y + 4
                            g.FillRectangle(bgBrush, textRect)
                            g.DrawString(text, font, textBrush, textRect.X + 2, textRect.Y + 2)
                        End Using
                    End Using
                End Using
            End If
        End If
    End Sub

    Private Sub CalculateRealCropCoordinates()
        If _currentMediaInfo.Width <= 0 OrElse _currentMediaInfo.Height <= 0 Then Return
        If InternalPreviewBox.Width <= 0 OrElse InternalPreviewBox.Height <= 0 Then Return

        Dim actualVideoWidth As Integer = (_currentMediaInfo.Width \ 2) * 2
        Dim actualVideoHeight As Integer = (_currentMediaInfo.Height \ 2) * 2

        If actualVideoWidth <= 0 Then actualVideoWidth = 2
        If actualVideoHeight <= 0 Then actualVideoHeight = 2

        Dim imageRatio As Double = actualVideoWidth / actualVideoHeight
        Dim controlRatio As Double = InternalPreviewBox.Width / InternalPreviewBox.Height
        Dim drawRect As System.Drawing.Rectangle

        If imageRatio > controlRatio Then
            Dim drawHeight As Integer = CInt(Math.Round(InternalPreviewBox.Width / imageRatio))
            Dim yOff As Integer = (InternalPreviewBox.Height - drawHeight) \ 2
            drawRect = New System.Drawing.Rectangle(0, yOff, InternalPreviewBox.Width, drawHeight)
        Else
            Dim drawWidth As Integer = CInt(Math.Round(InternalPreviewBox.Height * imageRatio))
            Dim xOff As Integer = (InternalPreviewBox.Width - drawWidth) \ 2
            drawRect = New System.Drawing.Rectangle(xOff, 0, drawWidth, InternalPreviewBox.Height)
        End If

        If drawRect.Width <= 0 OrElse drawRect.Height <= 0 Then
            ClearCrop()
            Return
        End If

        Dim intersectedRect As System.Drawing.Rectangle = System.Drawing.Rectangle.Intersect(_currentCropRect, drawRect)

        If intersectedRect.IsEmpty OrElse intersectedRect.Width <= 0 OrElse intersectedRect.Height <= 0 Then
            FinalCropX = 0 : FinalCropY = 0 : FinalCropW = 0 : FinalCropH = 0
            Return
        End If

        Dim scaleX As Double = actualVideoWidth / CDbl(drawRect.Width)
        Dim scaleY As Double = actualVideoHeight / CDbl(drawRect.Height)

        Dim realX As Integer = CInt(Math.Floor((intersectedRect.X - drawRect.X) * scaleX))
        Dim realY As Integer = CInt(Math.Floor((intersectedRect.Y - drawRect.Y) * scaleY))
        Dim realW As Integer = CInt(Math.Ceiling(intersectedRect.Width * scaleX))
        Dim realH As Integer = CInt(Math.Ceiling(intersectedRect.Height * scaleY))

        If realX < 0 Then realX = 0
        If realY < 0 Then realY = 0
        If realW < 2 Then realW = 2
        If realH < 2 Then realH = 2

        realX = (realX \ 2) * 2
        realY = (realY \ 2) * 2
        realW = (realW \ 2) * 2
        realH = (realH \ 2) * 2

        If realX + realW > actualVideoWidth Then
            realW = actualVideoWidth - realX
        End If

        If realY + realH > actualVideoHeight Then
            realH = actualVideoHeight - realY
        End If

        realW = (realW \ 2) * 2
        realH = (realH \ 2) * 2

        FinalCropX = realX
        FinalCropY = realY
        FinalCropW = realW
        FinalCropH = realH
    End Sub

    Public Sub ClearCrop()
        IsCropModeActive = False
        _currentCropRect = System.Drawing.Rectangle.Empty
        FinalCropX = 0
        FinalCropY = 0
        FinalCropW = 0
        FinalCropH = 0
        If InternalPreviewBox IsNot Nothing AndAlso Not InternalPreviewBox.IsDisposed Then
            InternalPreviewBox.Invalidate()
        End If
    End Sub

    Private Sub Form1_Shown(sender As Object, e As EventArgs) Handles Me.Shown
        If PictureBox1?.IsDisposed = False Then
            PictureBox1.Invalidate()
            PictureBox1.Refresh()
            If Not isClosing AndAlso PictureBox1 IsNot Nothing AndAlso Not PictureBox1.IsDisposed Then
                PushStateToRenderer()
            End If
        End If
    End Sub

    Private Async Function LoadBackgroundDataAsync() As Task
        Try
            Await InitializeHardwareAsync()
            SafeUIInvoke(Sub() Label2.Text = "Загрузка кодеков...")
            Await PreloadEncoders()

            SafeUIInvoke(Sub()
                             UpdateComboBox3()
                             UpdateComboBox4()

                             If Not _ffmpegService.CheckFFmpeg() Then
                                 MessageBox.Show("FFmpeg не найден." & vbCrLf & "Поместите ffmpeg.exe в системный PATH или в папку ffmpeg\bin.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error)
                                 DisableAllControls()
                                 Label2.Text = "Ошибка загрузки"
                             Else
                                 Label2.Text = "Готов к работе"
                                 DisableControls(False)
                             End If

                             If PictureBox1 IsNot Nothing AndAlso Not PictureBox1.IsDisposed Then
                                 PictureBox1.Invalidate()
                                 PictureBox1.Refresh()
                                 If Not isClosing AndAlso PictureBox1 IsNot Nothing AndAlso Not PictureBox1.IsDisposed Then
                                     PushStateToRenderer()
                                 End If
                             End If
                         End Sub)
        Catch ex As Exception
            SafeLog("Ошибка в LoadBackgroundDataAsync: " & ex.Message)
            SafeUIInvoke(Sub() DisableControls(False))
        End Try
    End Function

    Private Sub SaveMarkersForFile(ByVal filePath As String)
        If String.IsNullOrEmpty(filePath) Then Return
        Try
            Dim newData As New MarkerData With {
                .StartTime = _model.MarkerStart,
                .EndTime = _model.MarkerEnd,
                .IsZoomed = _model.IsZoomed,
                .ViewStart = _model.ViewStart,
                .ViewEnd = _model.ViewEnd
            }
            fileMarkers.AddOrUpdate(filePath, newData, Function(k, oldData) newData)
        Catch ex As Exception
            SafeLog("SaveMarkersForFile error: " & ex.Message)
        End Try
    End Sub

    Private Sub UpdateMarkerTimeLabel()
        If isClosing Then Return
        If Label6?.IsDisposed = False Then
            Dim duration As TimeSpan = _model.MarkerEnd - _model.MarkerStart
            If duration < TimeSpan.Zero Then duration = TimeSpan.Zero
            Label6.Text = $"Начало: {FormatTimeSpan(_model.MarkerStart)} | Конец: {FormatTimeSpan(_model.MarkerEnd)} | Длительность: {FormatTimeSpan(duration)}"
        End If
    End Sub

    Private Sub LoadMarkersForFile(ByVal filePath As String)
        If String.IsNullOrEmpty(filePath) Then Return

        Dim markers As MarkerData = Nothing
        If fileMarkers.TryGetValue(filePath, markers) Then
            _model.SetMarkers(markers.StartTime, markers.EndTime)

            If markers.IsZoomed Then
                _model.ZoomIn(markers.ViewStart, markers.ViewEnd)
            Else
                _model.ResetZoomHistory()
            End If
        Else
            _model.SetMarkers(TimeSpan.Zero, _model.TotalDuration)
            _model.ResetZoomHistory()

            Dim newData As New MarkerData With {
                .StartTime = _model.MarkerStart,
                .EndTime = _model.MarkerEnd,
                .IsZoomed = _model.IsZoomed,
                .ViewStart = _model.ViewStart,
                .ViewEnd = _model.ViewEnd}
            fileMarkers(filePath) = newData
        End If
    End Sub

    Private Sub SafeSetVideoViewPreviewImage(ByVal newBmp As Bitmap)
        If isClosing OrElse VideoPanel1 Is Nothing Then
            newBmp?.Dispose()
            Return
        End If

        SafeUIInvoke(Sub()
                         If VideoPanel1.IsDisposed Then
                             newBmp?.Dispose()
                             Return
                         End If

                         Dim oldImg As Image = Nothing
                         Try
                             If InternalPreviewBox IsNot Nothing AndAlso Not InternalPreviewBox.IsDisposed Then
                                 InternalPreviewBox.SizeMode = PictureBoxSizeMode.Zoom
                                 oldImg = InternalPreviewBox.Image
                                 InternalPreviewBox.Image = newBmp

                                 Dim hasTransform = _inspectorForm IsNot Nothing AndAlso _inspectorForm.Visible AndAlso _tileRendererRef?.SelectedClip IsNot Nothing

                                 If newBmp Is Nothing OrElse hasTransform Then
                                     InternalPreviewBox.Visible = False
                                 Else
                                     InternalPreviewBox.Visible = True
                                     InternalPreviewBox.BringToFront()
                                 End If
                             Else
                                 newBmp?.Dispose()
                             End If
                         Catch ex As Exception
                             SafeLog("Ошибка замены изображения SafeSetVideoViewPreviewImage: " & ex.Message)
                             newBmp?.Dispose()
                         Finally
                             Try
                                 oldImg?.Dispose()
                             Catch
                             End Try
                         End Try
                     End Sub)
    End Sub

    Private Async Sub TextBox1_TextChanged(sender As Object, e As EventArgs) Handles TextBox1.TextChanged
        If isClosing Then Return
        Try
            Dim newCts As New CancellationTokenSource()
            Dim oldCts As CancellationTokenSource = Interlocked.Exchange(_textChangeCts, newCts)
            If oldCts IsNot Nothing Then SafeCancelAndDisposeCTS(oldCts)

            Dim token As CancellationToken = newCts.Token
            Dim isCancelled As Boolean = False

            Try
                Await Task.Delay(400, token)
            Catch ex As TaskCanceledException
                isCancelled = True
            End Try

            If Not isCancelled AndAlso Not token.IsCancellationRequested Then
                Await _textChangeSemaphore.WaitAsync(token)
                Try
                    If Not token.IsCancellationRequested Then
                        Dim parsedFiles = FileManager.ParseInputFiles(TextBox1.Text)
                        If parsedFiles.Count > 0 Then
                            Await ImportMediaFilesAsync(parsedFiles, False)
                        End If
                    End If
                Finally
                    _textChangeSemaphore.Release()
                End Try
            End If

        Catch ex As OperationCanceledException
        Catch ex As Exception
            SafeLog("Ошибка в TextBox1_TextChanged: " & ex.Message)
        End Try
    End Sub

    Private Sub EvaluateLogicRules()
        If isClosing Then Return

        SafeUIInvoke(Sub()
                         If selectedFiles Is Nothing OrElse selectedFiles.Count = 0 Then
                             Button1.Enabled = True
                             ComboBox1.Enabled = True
                             ComboBox2.Enabled = True
                             ComboBox3.Enabled = True
                             ComboBox4.Enabled = True
                             If ComboBox5 IsNot Nothing Then ComboBox5.Enabled = True
                             Button3.Enabled = False
                             Return
                         End If

                         Dim format As String = If(ComboBox1.SelectedItem?.ToString(), "")
                         Dim targetType As TargetFormatType = TargetFormatType.Video
                         If format.Trim().StartsWith("Image", StringComparison.OrdinalIgnoreCase) Then
                             targetType = TargetFormatType.Image
                         ElseIf format.Trim().StartsWith("Audio", StringComparison.OrdinalIgnoreCase) Then
                             targetType = TargetFormatType.Audio
                         End If

                         Dim inputState As New MediaInputState With {
                             .HasImage = inputHasImage,
                             .HasAudio = inputHasAudio,
                             .HasVideoWithAudio = inputHasVideoWithAudio,
                             .HasVideoNoAudio = inputHasVideoNoAudio,
                             .IsAudioReplaced = _isAudioReplaced
                         }

                         Dim uiState As UIControlsState = UIStateRules.Evaluate(inputState, targetType)

                         If targetType = TargetFormatType.Audio Then
                             Button1.Enabled = inputState.EffectiveHasAudio
                             ComboBox2.Enabled = False
                             ComboBox3.Enabled = ComboBox3.Items.Count > 0 AndAlso ComboBox3.Text <> "Нет доступных кодеков"
                             ComboBox4.Enabled = ComboBox4.Items.Count > 0
                             If ComboBox5 IsNot Nothing Then ComboBox5.Enabled = False
                         Else
                             Button1.Enabled = uiState.CanExport
                             ComboBox2.Enabled = uiState.CanSelectHardware
                             ComboBox3.Enabled = uiState.CanSelectEncoder AndAlso ComboBox3.Items.Count > 0 AndAlso ComboBox3.Text <> "Нет доступных кодеков"
                             ComboBox4.Enabled = uiState.CanSelectCompression AndAlso ComboBox4.Items.Count > 0
                             If ComboBox5 IsNot Nothing Then ComboBox5.Enabled = uiState.CanSelectResolution
                         End If

                         If targetType = TargetFormatType.Audio AndAlso inputState.EffectiveHasAudio AndAlso ComboBox4.Items.Contains("192 kbps") Then
                             If ComboBox4.SelectedItem Is Nothing Then ComboBox4.SelectedItem = "192 kbps"
                         End If

                         If targetType = TargetFormatType.Video AndAlso inputState.EffectiveHasVideoWithAudio AndAlso ComboBox4.Items.Contains("Minimal") Then
                             If ComboBox4.SelectedItem Is Nothing Then ComboBox4.SelectedItem = "Minimal"
                         End If
                     End Sub)
    End Sub

    Private Async Function UpdateTimelineAsync(videoPath As String, segmentStart As TimeSpan, segmentEnd As TimeSpan, resetMarkers As Boolean) As Task
        If isClosing OrElse PictureBox1 Is Nothing OrElse PictureBox1.IsDisposed Then Return

        StartProLoading()
        DisableControls(True)

        Try
            Dim pbWidth As Integer = PictureBox1.ClientSize.Width
            Dim pbHeight As Integer = PictureBox1.ClientSize.Height
            If pbWidth < 100 OrElse pbHeight < 50 Then Return

            Dim info As FFmpegService.MediaInfo = Await _ffmpegService.GetMediaInfoAsync(videoPath)
            If isClosing Then Return

            Dim isAudioFile As Boolean = (info.Width = 0 AndAlso info.Height = 0)

            Dim newMst As TimeSpan = _model.MarkerStart
            Dim newMet As TimeSpan = _model.MarkerEnd
            If newMet = TimeSpan.Zero OrElse newMet > info.Duration Then newMet = info.Duration
            If newMst > newMet Then newMst = TimeSpan.Zero

            _model.SetMarkers(newMst, newMet)

            SafeCancelAndDisposeCTS(generateContactCts, generateLock)
            SyncLock generateLock
                generateContactCts = New CancellationTokenSource()
            End SyncLock

            SafeUIInvoke(Sub() UpdateFileInfoLabel(videoPath, info))

            Dim contentWidth As Integer = pbWidth
            Dim timelineRulerHeight As Integer = 14

            Dim audioTrackHeight As Integer = If(isAudioFile, pbHeight - timelineRulerHeight, CInt(Math.Max(30, pbHeight * 0.2F)))
            Dim trackHeight As Integer = pbHeight - timelineRulerHeight - audioTrackHeight
            If trackHeight < 10 Then trackHeight = 10

            Dim naturalAspect As Double = If(info.Height > 0, CDbl(info.Width) / info.Height, 16.0 / 9.0)
            Dim idealThumbWidth As Double = trackHeight * naturalAspect
            Dim naturalTileCount As Integer = CInt(Math.Ceiling(contentWidth / idealThumbWidth))
            Dim tileCount As Integer = Math.Max(8, naturalTileCount)
            Dim thumbWidth As Integer = CInt(Math.Ceiling(contentWidth / CDbl(tileCount)))
            thumbWidth = (thumbWidth \ 2) * 2
            trackHeight = (trackHeight \ 2) * 2
            If thumbWidth < 40 Then thumbWidth = 40

            Dim tileSize As New Size(thumbWidth, trackHeight)

            Dim segmentStartToUse As TimeSpan = If(_model.IsZoomed, _model.ViewStart, TimeSpan.Zero)
            Dim segmentEndToUse As TimeSpan = If(_model.IsZoomed, _model.ViewEnd, info.Duration)

            Dim visibleDurSec As Double = (segmentEndToUse - segmentStartToUse).TotalSeconds
            If visibleDurSec <= 0 Then visibleDurSec = info.Duration.TotalSeconds

            Dim idealInterval As Double = visibleDurSec / tileCount
            Dim singleFrameDuration As Double = If(info.Fps > 0, 1.0 / info.Fps, 0.0333333)

            Dim finalInterval As Double = Math.Max(idealInterval, singleFrameDuration)
            If finalInterval > 10.0 Then finalInterval = 10.0

            Dim intervalSeconds As Double = finalInterval
            Dim duration As TimeSpan = info.Duration

            _tileRenderer.UpdateLayout(tileSize, tileCount)
            _tileRenderer.Resize(pbWidth, pbHeight)

            ClearAllGpuCaches()

            If _gpuFramePool IsNot Nothing Then
                _gpuFramePool.DisposeAll()
                _gpuFramePool = Nothing
            End If

            If _scrubFramePool IsNot Nothing Then
                _scrubFramePool.DisposeAll()
                _scrubFramePool = Nothing
            End If

            Dim cacheHeight As Integer = CInt(Math.Ceiling(tileSize.Height * FrameQualityMultiplier))
            Dim cacheWidth As Integer = CInt(Math.Ceiling(cacheHeight * naturalAspect))
            cacheWidth = (cacheWidth \ 2) * 2
            cacheHeight = (cacheHeight \ 2) * 2

            Dim d3dDevice As SharpDX.Direct3D11.Device = Nothing
            If _tileRendererRef IsNot Nothing AndAlso _tileRendererRef.Device IsNot Nothing Then
                d3dDevice = _tileRendererRef.Device
            End If

            If d3dDevice IsNot Nothing Then
                _gpuFramePool = New GpuFramePool(d3dDevice, 150, cacheWidth, cacheHeight)

                Dim videoFiles = _model.Tracks.SelectMany(Function(t) t.Clips).
                                               Where(Function(c) c.MediaType = TargetFormatType.Video).
                                               Select(Function(c) c.FilePath).Distinct().ToList()

                For Each vFile In videoFiles
                    Dim cache = New GpuFrameCacheManager(_gpuFramePool, intervalSeconds, _model.TotalDuration, 150)
                    Dim extractor = New GpuFrameExtractor(cache, vFile, d3dDevice, d3dDevice.ImmediateContext)
                    AddHandler extractor.LogMessage, Sub(msg) SafeLog(msg)

                    _gpuFrameCaches.Add(vFile, cache)
                    _gpuFrameExtractors.Add(vFile, extractor)
                Next

                _tileRenderer.SetDataSources(_gpuFrameCaches, _gpuFrameExtractors)
            Else
                _tileRenderer.SetDataSources(Nothing, Nothing)
            End If

            Dim allMediaFiles = _model.Tracks.SelectMany(Function(t) t.Clips).
                                              Select(Function(c) c.FilePath).Distinct().ToList()

            For Each mFile In allMediaFiles
                Try
                    Dim actualAudioPath As String = mFile
                    If mFile = videoPath AndAlso _isAudioReplaced AndAlso Not String.IsNullOrEmpty(_externalAudioPath) Then
                        actualAudioPath = _externalAudioPath
                    End If

                    UpdateLabel($"Анализ аудио: {IO.Path.GetFileName(actualAudioPath)}...")
                    Dim peaks = Await _ffmpegService.GenerateAudioPeaksAsync(actualAudioPath, 256, CancellationToken.None)

                    If peaks IsNot Nothing AndAlso peaks.Length > 0 Then
                        _tileRendererRef?.AddAudioPeaksCache(mFile, peaks, 256)
                    End If
                Catch ex As Exception
                    SafeLog($"Ошибка генерации пиков для {mFile}: {ex.Message}")
                End Try
            Next

            For Each ext In _gpuFrameExtractors.Values
                Dim typedExt = TryCast(ext, GpuFrameExtractor)
                If typedExt IsNot Nothing Then
                    Dim preloadCount As Integer = Math.Min(15, 150)
                    For i As Integer = 0 To preloadCount - 1
                        Dim idx = i
                        Dim loadAction = Async Sub()
                                             Try
                                                 Await typedExt.EnsureFrameCachedAsync(idx, CancellationToken.None)
                                                 SafeUIInvoke(Sub()
                                                                  If PictureBox1 IsNot Nothing AndAlso Not PictureBox1.IsDisposed Then
                                                                      PictureBox1.Invalidate()
                                                                  End If
                                                              End Sub)
                                             Catch ex As Exception
                                             End Try
                                         End Sub
                        loadAction()
                    Next
                End If
            Next

            If resetMarkers Then
                _model.SetMarkers(TimeSpan.Zero, duration)
                _model.ResetZoomHistory()

                If selectedFiles.Count > 0 Then
                    Dim key As String = selectedFiles(0)
                    Dim value As MarkerData = fileMarkers.GetOrAdd(key, Function(k) New MarkerData())
                    value.StartTime = _model.MarkerStart
                    value.EndTime = _model.MarkerEnd
                    value.IsZoomed = False
                End If
            End If

            StopProLoading()

            SafeUIInvoke(Sub()
                             If PictureBox1 IsNot Nothing AndAlso Not PictureBox1.IsDisposed Then
                                 PushStateToRenderer()
                                 PictureBox1.Invalidate()
                             End If
                             UpdateMarkerTimeLabel()
                             If resetMarkers Then
                                 Dim discardTask As Task = UpdatePreviewFrame(TimeSpan.Zero).ContinueWith(Sub(t)
                                                                                                              If t.IsFaulted Then
                                                                                                                  SafeLog("Ошибка генерации превью после сброса маркеров: " & t.Exception.GetBaseException().Message)
                                                                                                              End If
                                                                                                          End Sub)
                             End If
                         End Sub)

        Catch ex As Exception
            SafeLog("UpdateTimelineAsync error: " & ex.Message)
            StopProLoading()
            SafeUIInvoke(Sub() Label2.Text = "Ошибка загрузки таймлайна")
        Finally
            DisableControls(False)
        End Try

        If Not isClosing AndAlso PictureBox1 IsNot Nothing AndAlso Not PictureBox1.IsDisposed Then
            PushStateToRenderer()
        End If
    End Function

    Private Async Sub PreviewTimer_Tick(sender As Object, e As EventArgs) Handles previewTimer.Tick
        previewTimer.Stop()
        If isClosing Then Return
        Try
            Await UpdatePreviewFrame(pendingPreviewTime)
        Catch ex As Exception
            SafeLog("PreviewTimer_Tick UpdatePreviewFrame error: " & ex.Message)
        End Try
    End Sub

    Private Async Function UpdatePreviewFrame(targetTime As TimeSpan, Optional force As Boolean = False) As Task
        If isClosing OrElse selectedFiles Is Nothing OrElse selectedFiles.Count = 0 Then Return

        If _playbackController IsNot Nothing AndAlso _playbackController.State = IServices.PlaybackState.Playing Then Return

        If Not force AndAlso (DateTime.Now - lastPreviewRequest).TotalMilliseconds < 80 Then
            pendingPreviewTime = targetTime
            previewTimer?.Stop()
            previewTimer?.Start()
            Return
        End If

        _lastPreviewTime = targetTime
        lastPreviewRequest = DateTime.Now

        Dim ctx = _model.GetVideoContextAtTime(targetTime)
        If ctx Is Nothing Then
            SafeSetVideoViewPreviewImage(Nothing)
            Return
        End If

        Dim activeClip = ctx.Clip
        Dim physicalTime = ctx.PhysicalTime
        Dim videoPath = activeClip.FilePath

        Dim currentRevision As Long = Interlocked.Increment(_previewGenerationRevision)
        Dim myCts As New CancellationTokenSource()
        Dim oldCts As CancellationTokenSource = Interlocked.Exchange(previewCts, myCts)

        If oldCts IsNot Nothing Then
            Try
                oldCts.Cancel()
                oldCts.Dispose()
            Catch
            End Try
        End If

        Try
            Dim extFile As String = Path.GetExtension(videoPath).ToLower()
            Dim isStaticImage As Boolean = {".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".avif", ".jxl"}.Contains(extFile)
            Dim isAudio As Boolean = {".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg"}.Contains(extFile)

            If isAudio Then Return
            Dim info As FFmpegService.MediaInfo = Await _ffmpegService.GetMediaInfoAsync(videoPath)
            If info.Width = 0 AndAlso info.Height = 0 AndAlso Not isStaticImage Then Return
            If Not isStaticImage AndAlso info.Duration = TimeSpan.Zero Then Return

            Dim baseW As Integer = 1920
            Dim baseH As Integer = 1080
            Dim targetW As Integer = baseW
            Dim targetH As Integer = baseH

            If info.Width > 0 AndAlso info.Height > 0 Then
                Dim sourceAspect As Double = info.Width / info.Height
                Dim targetAspect As Double = baseW / baseH

                If isStaticImage AndAlso info.Width <= 3840 AndAlso info.Height <= 2160 Then
                    targetW = info.Width
                    targetH = info.Height
                Else
                    If sourceAspect > targetAspect Then
                        targetW = baseW
                        targetH = CInt(baseW / sourceAspect)
                    Else
                        targetH = baseH
                        targetW = CInt(baseH * sourceAspect)
                    End If
                End If

                targetW = (targetW \ 2) * 2
                targetH = (targetH \ 2) * 2
                If targetW = 0 Then targetW = 2
                If targetH = 0 Then targetH = 2
            End If

            Dim cacheKey As String = $"{videoPath}_{physicalTime.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)}_{targetW}x{targetH}_{isAudio}"

            Dim handle As PreviewCacheManager.CachedFrameHandle = _previewCache.TryGet(cacheKey, targetW, targetH)

            If handle Is Nothing Then
                Dim newFrame As PooledFrameBuffer = Await _ffmpegService.ExtractPreviewFrameFromPipeAsync(videoPath, physicalTime, targetW, targetH, myCts.Token)

                If Not isClosing AndAlso Not myCts.Token.IsCancellationRequested AndAlso newFrame IsNot Nothing AndAlso newFrame.Size > 0 Then
                    _previewCache.Add(cacheKey, newFrame, targetW, targetH)
                    handle = _previewCache.TryGet(cacheKey, targetW, targetH)
                Else
                    newFrame?.Dispose()
                    newFrame = Nothing
                End If
            End If

            If handle IsNot Nothing Then
                Using handle
                    Dim cachedFrame As PooledFrameBuffer = handle.Buffer

                    If cachedFrame IsNot Nothing AndAlso cachedFrame.Size > 0 AndAlso currentRevision = Interlocked.Read(_previewGenerationRevision) Then
                        Dim previewBmp As Bitmap = Nothing
                        Dim success As Boolean = False
                        Try
                            previewBmp = New Bitmap(targetW, targetH, Imaging.PixelFormat.Format32bppArgb)
                            Dim bmpData As Imaging.BitmapData = previewBmp.LockBits(New System.Drawing.Rectangle(0, 0, targetW, targetH), Imaging.ImageLockMode.WriteOnly, previewBmp.PixelFormat)
                            Try
                                System.Runtime.InteropServices.Marshal.Copy(cachedFrame.Buffer, 0, bmpData.Scan0, cachedFrame.Size)
                            Finally
                                previewBmp.UnlockBits(bmpData)
                            End Try

                            If isClosing Then
                                previewBmp.Dispose()
                                Return
                            End If

                            SafeUIInvoke(Sub()
                                             Try
                                                 If currentRevision = Interlocked.Read(_previewGenerationRevision) Then
                                                     SafeSetVideoViewPreviewImage(previewBmp)
                                                 Else
                                                     previewBmp?.Dispose()
                                                 End If
                                             Catch ex As Exception
                                                 previewBmp?.Dispose()
                                             End Try
                                         End Sub,
                                         Sub()
                                             previewBmp?.Dispose()
                                         End Sub)
                            success = True
                        Catch ex As Exception
                            Throw
                        Finally
                            If Not success AndAlso previewBmp IsNot Nothing Then
                                previewBmp.Dispose()
                            End If
                        End Try
                    End If
                End Using
            End If

        Catch ex As OperationCanceledException
        Catch ex As ObjectDisposedException
        Catch ex As Exception
            SafeLog("UpdatePreviewFrame error: " & ex.Message)
        End Try
    End Function

    Private Sub SafeCancelAndDisposeCTS(ByRef cts As CancellationTokenSource, Optional ByVal lockObj As Object = Nothing)
        Dim oldCts As CancellationTokenSource

        If lockObj IsNot Nothing Then
            SyncLock lockObj
                oldCts = Interlocked.Exchange(cts, Nothing)
            End SyncLock
        Else
            oldCts = Interlocked.Exchange(cts, Nothing)
        End If

        If oldCts IsNot Nothing Then
            Try
                oldCts.Cancel()
            Catch ex As Exception
                SafeLog("Ошибка отмены CTS: " & ex.Message)
            End Try
        End If
    End Sub

    Private Sub PictureBox1_Resize(sender As Object, e As EventArgs) Handles PictureBox1.Resize
        If isClosing Then Return
        resizeDebounceTimer.Stop()
        resizeDebounceTimer.Start()
    End Sub

    Private Async Function TriggerPreviewNowAsync(virtualTime As TimeSpan) As Task
        If isClosing Then Return
        Try
            Await UpdatePreviewFrame(virtualTime, True).ConfigureAwait(False)
        Catch ex As Exception
            SafeLog("Ошибка в TriggerPreviewNowAsync: " & ex.Message)
        End Try
    End Function

    Private Async Sub ResizeDebounceTimer_Tick(sender As Object, e As EventArgs) Handles resizeDebounceTimer.Tick
        resizeDebounceTimer.Stop()
        If isClosing OrElse resizeInProgress OrElse selectedFiles.Count = 0 OrElse _model.TotalDuration <= TimeSpan.Zero Then Return

        resizeInProgress = True
        Try
            UpdateLabel("Обновление таймлайна после изменения размера...")
            Dim startSeg As TimeSpan = If(_model.IsZoomed, _model.ViewStart, TimeSpan.Zero)
            Dim endSeg As TimeSpan = If(_model.IsZoomed, _model.ViewEnd, TimeSpan.Zero)
            Await UpdateTimelineAsync(selectedFiles(0), startSeg, endSeg, False)

            If Not isClosing AndAlso PictureBox1 IsNot Nothing AndAlso Not PictureBox1.IsDisposed Then
                PushStateToRenderer()
            End If
            UpdateMarkerTimeLabel()
        Catch ex As Exception
            SafeLog("Ошибка при изменении размера: " & ex.Message)
        Finally
            resizeInProgress = False
            UpdateLabel("Готов")
        End Try
    End Sub

    Private Async Function PreloadEncoders() As Task
        Try
            availableEncoders = Await GetAvailableEncoders()
            isEncodersLoaded = True
        Catch ex As Exception
            SafeLog("Ошибка PreloadEncoders: " & ex.Message)
            availableEncoders = New List(Of String)()
            isEncodersLoaded = True
        End Try
    End Function

    Private Async Function GetAvailableEncoders() As Task(Of List(Of String))
        Dim encoders As New List(Of String)()
        Try
            If Not _ffmpegService.CheckFFmpeg() Then Return encoders

            Dim res = Await _ffmpegService.RunProcessCaptureAsync(_ffmpegService.GetFFmpegPath(), "-hide_banner -encoders", 5000, CancellationToken.None)
            Dim output As String = If(String.IsNullOrEmpty(res.StdOut), res.StdErr, res.StdOut & vbCrLf & res.StdErr)

            Dim lines As String() = output.Split({vbCrLf, vbLf}, StringSplitOptions.RemoveEmptyEntries)

            For Each raw As String In lines
                Dim line As String = raw.Trim()
                If line.Length = 0 Then Continue For
                If line.StartsWith("------") OrElse line.StartsWith("Encoders:") OrElse line.StartsWith("----") Then Continue For

                Dim parts As String() = line.Split(" "c, StringSplitOptions.RemoveEmptyEntries)
                If parts.Length >= 2 Then
                    Dim name As String = parts(1).Trim()
                    If Not String.IsNullOrEmpty(name) Then
                        Dim lower As String = name.ToLowerInvariant()
                        If Not encoders.Contains(lower) Then
                            encoders.Add(lower)
                        End If
                    End If
                End If
            Next
        Catch ex As Exception
            SafeLog("Ошибка GetAvailableEncoders: " & ex.Message)
        End Try
        Return encoders.Distinct().ToList()
    End Function

    Private Function Get_hardwareMonitor1() As HardwareMonitorService
        Return _hardwareMonitor
    End Function

    Private Async Function InitializeHardwareAsync() As Task
        Try
            Dim result = Await _hardwareMonitor.ScanHardwareAsync()
            SafeUIInvoke(Sub() UpdateHardwareComboBox(result.AllItems, result.FoundGpuName))
        Catch ex As Exception
            SafeLog("Ошибка InitializeHardwareAsync: " & ex.Message)
        End Try
    End Function

    Private Sub UpdateHardwareComboBox(items As List(Of String), gpuFoundName As String)
        If isClosing OrElse ComboBox2?.IsDisposed = True Then Return

        ComboBox2.Items.Clear()

        If items IsNot Nothing AndAlso items.Count > 0 Then
            ComboBox2.Items.AddRange(items.ToArray())
        End If

        Dim cpuLabel As String = "CPU (Fallback)"
        If Not ComboBox2.Items.Contains(cpuLabel) Then
            ComboBox2.Items.Add(cpuLabel)
        End If

        If ComboBox2.Items.Count > 0 Then
            If Not String.IsNullOrEmpty(gpuFoundName) AndAlso ComboBox2.Items.Contains(gpuFoundName) Then
                ComboBox2.SelectedItem = gpuFoundName
                isCpuSelected = False
            Else
                If ComboBox2.Items.Contains(cpuLabel) Then
                    ComboBox2.SelectedItem = cpuLabel
                    isCpuSelected = True
                Else
                    ComboBox2.SelectedIndex = 0
                    isCpuSelected = True
                End If
            End If
        End If

        UpdateHardwareSelection()
    End Sub

    Private Sub UpdateHardwareSelection()
        If isClosing OrElse ComboBox2?.IsDisposed = True OrElse ComboBox2.SelectedItem Is Nothing Then
            isCpuSelected = True
            isNvidiaGpuSelected = False
            isAMDGpuSelected = False
            Return
        End If

        Dim sel As String = ComboBox2.SelectedItem.ToString()
        isNvidiaGpuSelected = sel.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
        isAMDGpuSelected = sel.Contains("AMD", StringComparison.OrdinalIgnoreCase) AndAlso Not isNvidiaGpuSelected

        isCpuSelected = sel.Contains("CPU", StringComparison.OrdinalIgnoreCase) OrElse (Not isNvidiaGpuSelected AndAlso Not isAMDGpuSelected)
    End Sub

    Private Sub InitializeUI()
        ComboBox1.Items.AddRange(_formats)
        ComboBox1.SelectedIndex = 0

        If ComboBox5 IsNot Nothing Then
            ComboBox5.Items.Clear()
            ComboBox5.Items.AddRange(ResolutionProfiles.ToArray())
            ComboBox5.SelectedIndex = 0
        End If

        ProgressBar1.Minimum = 0
        ProgressBar1.Maximum = 100
        ProgressBar1.Value = 0

        Button1.Enabled = True
        ComboBox1.Enabled = True
        ComboBox2.Enabled = True
        ComboBox3.Enabled = True
        ComboBox4.Enabled = True
        Button3.Enabled = False

        Label2.Text = "Готов к работе"

    End Sub

    Private Sub ComboBox1_SelectedIndexChanged(sender As Object, e As EventArgs) Handles ComboBox1.SelectedIndexChanged
        If isClosing OrElse ComboBox1 Is Nothing OrElse ComboBox2 Is Nothing Then Return

        Dim format As String = If(ComboBox1.SelectedItem?.ToString(), "")
        Dim isAudioOrImage As Boolean = format.Trim().StartsWith("Audio", StringComparison.OrdinalIgnoreCase) OrElse
                                        format.Trim().StartsWith("Image", StringComparison.OrdinalIgnoreCase)

        If isAudioOrImage Then
            If ComboBox2.Enabled AndAlso ComboBox2.SelectedIndex <> -1 Then
                lastHardwareIndex = ComboBox2.SelectedIndex
            End If

            Dim cpuIndex As Integer = -1
            For i As Integer = 0 To ComboBox2.Items.Count - 1
                If ComboBox2.Items(i).ToString().Contains("CPU", StringComparison.OrdinalIgnoreCase) Then
                    cpuIndex = i
                    Exit For
                End If
            Next
            If cpuIndex >= 0 Then
                ComboBox2.SelectedIndex = cpuIndex
            ElseIf ComboBox2.Items.Count > 0 Then
                ComboBox2.SelectedIndex = 0
            End If

            Label2.Text = "Для аудио и изображений используется только процессор (CPU)."
        Else
            If lastHardwareIndex >= 0 AndAlso lastHardwareIndex < ComboBox2.Items.Count Then
                ComboBox2.SelectedIndex = lastHardwareIndex
            End If

            If Label2.Text = "Для аудио и изображений используется только процессор (CPU)." Then
                Label2.Text = "Готов к работе"
            End If
        End If

        UpdateComboBox3()
        UpdateComboBox4()
        EvaluateLogicRules()
    End Sub

    Private Sub ComboBox2_SelectedIndexChanged(sender As Object, e As EventArgs) Handles ComboBox2.SelectedIndexChanged
        If isClosing OrElse ComboBox2?.IsDisposed = True Then Return

        If ComboBox2.Enabled AndAlso ComboBox2.SelectedIndex <> -1 Then
            lastHardwareIndex = ComboBox2.SelectedIndex
        End If

        UpdateHardwareSelection()
        UpdateComboBox3()
        UpdateComboBox4()
        EvaluateLogicRules()
    End Sub

    Private Sub ComboBox3_SelectedIndexChanged(sender As Object, e As EventArgs) Handles ComboBox3.SelectedIndexChanged
        UpdateComboBox4()
    End Sub

    Private Sub UpdateComboBox3()
        If isClosing OrElse ComboBox1 Is Nothing OrElse ComboBox3 Is Nothing OrElse Not isEncodersLoaded Then Return

        Dim format As String = ComboBox1.SelectedItem?.ToString()
        ComboBox3.Items.Clear()
        ComboBox3.Text = String.Empty

        If String.IsNullOrEmpty(format) Then Return

        Dim baseCodecs As List(Of String) = Nothing
        If FormatToCodecs.TryGetValue(format, baseCodecs) Then
            Dim supportedLabels = baseCodecs.Where(Function(c) IsCodecSupportedOnHardware(c)).Select(Function(c) GetCodecLabel(c)).ToArray()
            If supportedLabels.Length <> 0 Then
                ComboBox3.Items.AddRange(supportedLabels)
                ComboBox3.SelectedIndex = 0
            Else
                ComboBox3.Text = "Нет доступных кодеков"
            End If
        End If
    End Sub

    Private Sub UpdateComboBox4()
        If isClosing OrElse ComboBox1 Is Nothing OrElse ComboBox4 Is Nothing Then Return

        Dim format As String = If(ComboBox1.SelectedItem?.ToString(), "")
        ComboBox4.Items.Clear()
        ComboBox4.Text = ""

        Dim isAnimated As Boolean = _currentMediaInfo.Duration > TimeSpan.Zero OrElse _currentMediaInfo.Fps > 0
        Dim trimmedFormat As String = format.Trim()

        If trimmedFormat.StartsWith("Audio", StringComparison.OrdinalIgnoreCase) Then
            ComboBox4.Items.AddRange({"64 kbps", "128 kbps", "192 kbps", "256 kbps", "320 kbps"})
            If ComboBox4.Items.Count > 0 Then ComboBox4.SelectedIndex = 2
            Return
        End If

        If trimmedFormat.StartsWith("Image", StringComparison.OrdinalIgnoreCase) Then
            If format = "Image GIF" Then
                If isAnimated Then
                    ComboBox4.Items.AddRange({"Minimal", "Low", "Medium", "High", "Maximum"})
                    If ComboBox4.Items.Count > 0 Then ComboBox4.SelectedIndex = 0
                End If
            Else
                ComboBox4.Items.AddRange({"Minimal", "Low", "Medium", "High", "Maximum"})
                If ComboBox4.Items.Count > 0 Then ComboBox4.SelectedIndex = 0
            End If
            Return
        End If

        ComboBox4.Items.AddRange({"Minimal", "Low", "Medium", "High", "Maximum"})
        If ComboBox4.Items.Count > 0 Then ComboBox4.SelectedIndex = 0
    End Sub

    Private Function IsCodecSupportedOnHardware(codec As String) As Boolean
        If availableEncoders Is Nothing Then Return False
        If isCpuSelected Then
            Dim cpuEncoder As String = GetEncoderForCodec(codec, True)
            Return Not String.IsNullOrEmpty(cpuEncoder) AndAlso CheckCodec(cpuEncoder)
        End If

        If LegacyAndCpuOnlyCodecs.Contains(codec) Then Return False

        Dim encoder As String = GetEncoderForCodec(codec, False)
        If String.IsNullOrEmpty(encoder) Then Return False
        If Not CheckCodec(encoder) Then Return False

        Dim isGpuEncoder As Boolean = encoder.Contains("nvenc") OrElse encoder.Contains("amf")
        If isGpuEncoder AndAlso codec = "AV1" Then
            Dim selectedName As String = If(ComboBox2.SelectedItem?.ToString(), "")
            Dim nvGen As Integer = _hardwareMonitor.GetNvidiaGeneration(selectedName)
            Dim amdGen As Integer = _hardwareMonitor.GetAmdGeneration(selectedName)
            If isNvidiaGpuSelected AndAlso nvGen > 0 AndAlso nvGen < 4000 Then Return False
            If isAMDGpuSelected AndAlso amdGen > 0 AndAlso amdGen < 7000 Then Return False
        End If

        Return True
    End Function

    Private Function GetCodecLabel(codec As String) As String
        If LegacyAndCpuOnlyCodecs.Contains(codec) Then Return codec & " (CPU)"
        If isCpuSelected Then Return codec & " (CPU)"
        If isNvidiaGpuSelected Then Return "NVIDIA " & codec
        If isAMDGpuSelected Then Return "AMD " & codec
        Return codec & " (CPU)"
    End Function

    Private Function CheckCodec(codecName As String) As Boolean
        If availableEncoders Is Nothing Then Return False
        Return availableEncoders.Any(Function(x As String) String.Equals(x, codecName, StringComparison.OrdinalIgnoreCase))
    End Function

    Private Function GetEncoderForCodec(codec As String, Optional forceCpu As Boolean = False) As String
        If Not forceCpu AndAlso Not isCpuSelected Then
            If isNvidiaGpuSelected Then
                Select Case codec
                    Case "H.264" : Return "h264_nvenc"
                    Case "H.265" : Return "hevc_nvenc"
                    Case "AV1" : Return "av1_nvenc"
                End Select
            ElseIf isAMDGpuSelected Then
                Select Case codec
                    Case "H.264" : Return "h264_amf"
                    Case "H.265" : Return "hevc_amf"
                    Case "AV1" : Return "av1_amf"
                End Select
            End If
        End If

        Dim baseEncoder As String = Nothing
        If CodecToBaseEncoder.TryGetValue(codec, baseEncoder) Then Return baseEncoder
        Return ""
    End Function

    Private Async Sub Button1_Click(sender As Object, e As EventArgs) Handles Button1.Click
        If Not Button1.Enabled OrElse selectedFiles Is Nothing OrElse selectedFiles.Count = 0 Then
            UpdateLabel("Исходный файл не выбран для выполнения экспорта.")
            Return
        End If

        Dim extractTime As TimeSpan = _lastPreviewTime
        If _playbackController IsNot Nothing AndAlso (_playbackController.State = IServices.PlaybackState.Playing OrElse _playbackController.CurrentVirtualTime > TimeSpan.Zero) Then
            extractTime = _playbackController.CurrentPhysicalTime
        End If
        If extractTime = TimeSpan.Zero AndAlso _model.MarkerStart > TimeSpan.Zero Then
            extractTime = _model.MarkerStart
        End If

        Dim targetW As Integer = 0
        Dim targetH As Integer = 0
        If ComboBox5 IsNot Nothing AndAlso ComboBox5.SelectedItem IsNot Nothing Then
            Dim selectedRes As Object = ComboBox5.SelectedItem
            Dim wProp = selectedRes.GetType().GetProperty("Width")
            Dim hProp = selectedRes.GetType().GetProperty("Height")
            If wProp IsNot Nothing Then targetW = CInt(wProp.GetValue(selectedRes, Nothing))
            If hProp IsNot Nothing Then targetH = CInt(hProp.GetValue(selectedRes, Nothing))
        End If

        Dim selectedFormat As String = "MP4 (MPEG-4)"
        If ComboBox1.SelectedItem IsNot Nothing Then selectedFormat = ComboBox1.SelectedItem.ToString()

        Dim videoEncoder As String = "H.264 (x264 Software)"
        If ComboBox3.SelectedItem IsNot Nothing Then videoEncoder = ComboBox3.SelectedItem.ToString()

        Dim compressionLevel As String = "Medium"
        If ComboBox4.SelectedItem IsNot Nothing Then compressionLevel = ComboBox4.SelectedItem.ToString()

        Dim options As New ExportOptions With {
            .SourceFile = selectedFiles(0),
            .SelectedFormat = selectedFormat,
            .VideoEncoder = videoEncoder,
            .CompressionLevel = compressionLevel,
            .TargetWidth = targetW,
            .TargetHeight = targetH,
            .CropW = If(IsCropModeActive AndAlso FinalCropW > 0, FinalCropW, 0),
            .CropH = If(IsCropModeActive AndAlso FinalCropH > 0, FinalCropH, 0),
            .CropX = If(IsCropModeActive AndAlso FinalCropW > 0, FinalCropX, 0),
            .CropY = If(IsCropModeActive AndAlso FinalCropH > 0, FinalCropY, 0),
            .InputHasImage = inputHasImage,
            .ExtractTime = extractTime,
            .IsNvidiaGpuSelected = isNvidiaGpuSelected,
            .IsAmdGpuSelected = isAMDGpuSelected,
            .IsAudioReplaced = _isAudioReplaced,
            .ExternalAudioPath = _externalAudioPath,
            .AudioOffset = _audioOffset,
            .TrackVolume = _trackVolume,
            .SourceFps = _currentMediaInfo.Fps
        }

        Dim renderer As TileTimelineRenderer = TryCast(_tileRendererRef, TileTimelineRenderer)

        If renderer IsNot Nothing Then
            options.AudioFadeIn = renderer.AudioFadeIn
            options.AudioFadeOut = renderer.AudioFadeOut
            options.VideoFadeIn = renderer.VideoFadeIn
            options.VideoFadeOut = renderer.VideoFadeOut

        Else
            options.AudioFadeIn = TimeSpan.Zero
            options.AudioFadeOut = TimeSpan.Zero
            options.VideoFadeIn = TimeSpan.Zero
            options.VideoFadeOut = TimeSpan.Zero
        End If

        Await _presenter.ExportMediaAsync(options)
    End Sub

    Private Sub SetUiExportState(enabled As Boolean)
        If Button1 IsNot Nothing Then Button1.Enabled = enabled
        If Button3 IsNot Nothing Then Button3.Enabled = Not enabled
        If ComboBox1 IsNot Nothing Then ComboBox1.Enabled = enabled
        If ToolStrip1 IsNot Nothing Then ToolStrip1.Enabled = enabled

        If enabled Then
            EvaluateLogicRules()
        Else
            If ComboBox2 IsNot Nothing Then ComboBox2.Enabled = False
            If ComboBox3 IsNot Nothing Then ComboBox3.Enabled = False
            If ComboBox4 IsNot Nothing Then ComboBox4.Enabled = False
            If ComboBox5 IsNot Nothing Then ComboBox5.Enabled = False
        End If
    End Sub

    Private Sub Button3_Click(sender As Object, e As EventArgs) Handles Button3.Click
        Button3.Enabled = False

        If MessageBox.Show("Остановить текущую конвертацию?", "Подтверждение", MessageBoxButtons.YesNo, MessageBoxIcon.Question) <> System.Windows.Forms.DialogResult.Yes Then
            Button3.Enabled = True
            Return
        End If

        UpdateLabel("Остановка конвертации (ожидание FFmpeg)...")
        _presenter.CancelExport()
    End Sub

    Private Sub UpdateLabel(ByVal text As String)
        SafeUIInvoke(Sub()
                         If Label2?.IsDisposed = False Then Label2.Text = text
                     End Sub)
    End Sub

    Private Sub DisableControls(disable As Boolean)
        SafeUIInvoke(Sub()
                         If disable Then
                             Button1.Enabled = False
                             Button3.Enabled = True
                             ComboBox1.Enabled = False
                             ComboBox2.Enabled = False
                             ComboBox3.Enabled = False
                             ComboBox4.Enabled = False
                             If ComboBox5 IsNot Nothing Then ComboBox5.Enabled = False
                             If ToolStrip1 IsNot Nothing Then ToolStrip1.Enabled = False
                         Else
                             Button3.Enabled = False
                             ComboBox1.Enabled = True
                             If ToolStrip1 IsNot Nothing Then ToolStrip1.Enabled = True
                             EvaluateLogicRules()
                         End If
                     End Sub)
    End Sub

    Private Sub DisableAllControls()
        DisableControls(True)
    End Sub

    Private Async Sub Form1_FormClosing(sender As Object, e As FormClosingEventArgs) Handles Me.FormClosing
        If _isCleanupComplete Then Return

        e.Cancel = True
        isClosing = True
        RemoveHandler ThemeManager.ThemeChanged, AddressOf OnGlobalThemeChanged

        Me.Hide()

        Try
            If InternalPreviewBox IsNot Nothing Then
                Dim currentImg As Image = InternalPreviewBox.Image
                InternalPreviewBox.Image = Nothing
                currentImg?.Dispose()
            End If
        Catch ex As Exception
            SafeLog("Ошибка освобождения превью при закрытии формы: " & ex.Message)
        End Try

        If popoutForm?.IsDisposed = False Then
            Try
                RemoveHandler popoutForm.FormClosed, AddressOf PopoutForm_Closed
                If VideoPanel1?.IsDisposed = False Then
                    VideoPanel1.Parent = Me
                End If
                popoutForm?.Close()
                popoutForm?.Dispose()
                popoutForm = Nothing
            Catch ex As Exception
                SafeLog("Ошибка при закрытии popoutForm: " & ex.Message)
            End Try
        End If

        SafeCancelAndDisposeCTS(generateContactCts, generateLock)
        Try
            If _scrubCts IsNot Nothing Then _scrubCts.Cancel()
            If _scrubLoopCts IsNot Nothing Then _scrubLoopCts.Cancel()
            previewTimer?.Stop()
            previewTimer?.Dispose()
            resizeDebounceTimer?.Stop()
            resizeDebounceTimer?.Dispose()
            LoadingTimer?.Stop()
            LoadingTimer?.Dispose()
            StopPlaybackUIUpdateLoop()
        Catch ex As Exception
            SafeLog("Ошибка остановки таймеров: " & ex.Message)
        End Try

        If _playbackController IsNot Nothing Then
            Try
                _playbackController.StopPlayback()
                _playbackController.Dispose()
                _playbackController = Nothing
            Catch : End Try
        End If

        If _audioPlayer IsNot Nothing Then
            Try
                _audioPlayer.Dispose()
                _audioPlayer = Nothing
            Catch : End Try
        End If

        Try
            _previewCache.Clear()
        Catch : End Try

        Try
            _videoPlayer?.Dispose()
        Catch : End Try

        Try
            _tileRenderer?.Dispose()
        Catch : End Try

        Try
            _gpuFramePool?.DisposeAll()
        Catch : End Try

        Try
            _scrubFramePool?.DisposeAll()
        Catch : End Try

        Try
            _asyncDecoder?.Dispose()
        Catch : End Try

        ClearAllGpuCaches()

        Try
            _scrubCts?.Dispose()
            _scrubLoopCts?.Dispose()
        Catch : End Try

        Dim cleanupTask As Task = Task.Run(Sub()
                                               Try
                                                   _ffmpegService?.Dispose()
                                               Catch ex As Exception
                                                   SafeLog("Ошибка очистки _ffmpegService: " & ex.Message)
                                               End Try
                                               Try
                                                   DeleteBakedAudioFile()
                                                   DeleteProxyVideoFile()
                                               Catch : End Try
                                           End Sub)

        Dim timeoutTask As Task = Task.Delay(2500)
        Dim completedTask As Task = Await Task.WhenAny(cleanupTask, timeoutTask)

        If completedTask Is timeoutTask Then
            SafeLog("Таймаут очистки ресурсов. Принудительное завершение приложения.")
        End If

        Try
            Log.CloseAndFlush()
        Catch : End Try

        _isCleanupComplete = True
        Environment.Exit(0)
    End Sub

    Private Sub SafeLog(msg As String)
        Try
            Log.Information(msg)
        Catch ex As Exception
            Debug.WriteLine("SafeLog: Внутренняя ошибка логирования: " & ex.Message)
        End Try
    End Sub


    Private Async Sub ToolStripButton1_Click(sender As Object, e As EventArgs) Handles ToolStripButton1.Click
        If _isPlayPauseProcessing Then Return
        _isPlayPauseProcessing = True
        ToolStripButton1.Enabled = False

        Try
            If selectedFiles.Count > 0 Then
                If _model.TotalDuration <= TimeSpan.Zero Then
                    MessageBox.Show("Данный файл распознан как статичное изображение. Воспроизведение анимации невозможно.", "Информация", MessageBoxButtons.OK, MessageBoxIcon.Information)
                    _isPlayPauseProcessing = False
                    ToolStripButton1.Enabled = True
                    Return
                End If

                If _playbackController IsNot Nothing Then
                    Dim state = _playbackController.State

                    If state = IServices.PlaybackState.Playing Then
                        _playbackController.Pause()
                        StopPlaybackUIUpdateLoop()
                        _awaitingFirstValidTime = False

                        If _tileRendererRef IsNot Nothing Then _tileRendererRef.IsMediaPlaying = False
                        UpdateLabel("Пауза")
                        UpdateDynamicStatus(True)
                    ElseIf state = IServices.PlaybackState.Paused Then

                        SafeSetVideoViewPreviewImage(Nothing)

                        _playbackController.ResumePlayback()
                        StartPlaybackUIUpdateLoop()
                        _awaitingFirstValidTime = False

                        If _tileRendererRef IsNot Nothing Then _tileRendererRef.IsMediaPlaying = True
                        UpdateLabel("Воспроизведение...")
                        UpdateDynamicStatus(True)
                    Else
                        _audioPlayer?.StopPlayback()
                        SetVolumeFromTrackBar()

                        Dim virtStart As TimeSpan = _currentVirtualPlaybackTime
                        If virtStart = TimeSpan.Zero OrElse virtStart >= _model.PhysicalToVirtualTime(_model.MarkerEnd) Then
                            virtStart = _model.PhysicalToVirtualTime(_model.MarkerStart)
                        End If

                        _awaitingFirstValidTime = True
                        Dim audioForMuteSignal As String = String.Empty
                        If _isAudioReplaced Then
                            audioForMuteSignal = _externalAudioPath
                        End If

                        SafeSetVideoViewPreviewImage(Nothing)

                        Await _playbackController.PlayAsync(selectedFiles(0), virtStart, audioForMuteSignal)

                        If _tileRendererRef IsNot Nothing Then _tileRendererRef.IsMediaPlaying = True
                        UpdateLabel("Воспроизведение...")
                        StartPlaybackUIUpdateLoop()
                        UpdateDynamicStatus(True)
                    End If

                    If PictureBox1 IsNot Nothing AndAlso Not PictureBox1.IsDisposed Then
                        PushStateToRenderer()
                    End If
                End If
            Else
                MessageBox.Show("Файл не загружен.", "Инфо", MessageBoxButtons.OK, MessageBoxIcon.Information)
            End If
        Catch ex As Exception
            SafeLog("Ошибка в ToolStripButton1_Click: " & ex.Message)
            UpdateLabel("Ошибка воспроизведения")
        End Try

        Try
            Await Task.Delay(350)
        Catch ex As Exception
        End Try

        _isPlayPauseProcessing = False
        ToolStripButton1.Enabled = True
    End Sub

    Private Sub ToolStripButton2_Click(sender As Object, e As EventArgs) Handles ToolStripButton2.Click
        Try
            _playbackController?.StopPlayback()
            StopPlaybackUIUpdateLoop()
            _awaitingFirstValidTime = False

            If _tileRendererRef IsNot Nothing Then _tileRendererRef.IsMediaPlaying = False

            _currentVirtualPlaybackTime = _model.PhysicalToVirtualTime(_model.MarkerStart)
            _tileRenderer?.UpdatePlayhead(_currentVirtualPlaybackTime)

            UpdateLabel("Воспроизведение остановлено")
            UpdateDynamicStatus(True)

            Dim discardTask As Task = TriggerPreviewNowAsync(_currentVirtualPlaybackTime)
        Catch ex As Exception
            SafeLog("Ошибка при остановке воспроизведения: " & ex.Message)
        End Try
    End Sub

    Private Async Sub ToolStripButton3_Click(sender As Object, e As EventArgs) Handles ToolStripButton3.Click
        Try
            If _playbackController Is Nothing Then
                UpdateLabel("Плеер не инициализирован")
                Return
            End If

            If selectedFiles Is Nothing OrElse selectedFiles.Count = 0 OrElse _model.TotalDuration <= TimeSpan.Zero Then
                UpdateLabel("Нет загруженного медиафайла")
                Return
            End If

            Dim state = _playbackController.State

            If state = IServices.PlaybackState.Playing Then
                _playbackController.Pause()
                StopPlaybackUIUpdateLoop()
                _awaitingFirstValidTime = False
                If _tileRendererRef IsNot Nothing Then _tileRendererRef.IsMediaPlaying = False
                UpdateLabel("Воспроизведение приостановлено")
            ElseIf state = IServices.PlaybackState.Paused Then

                SafeSetVideoViewPreviewImage(Nothing)

                _playbackController.ResumePlayback()
                StartPlaybackUIUpdateLoop()
                _awaitingFirstValidTime = False
                If _tileRendererRef IsNot Nothing Then _tileRendererRef.IsMediaPlaying = True
                UpdateLabel("Воспроизведение возобновлено")
            Else
                Dim virtStart = _currentVirtualPlaybackTime
                If virtStart >= _model.PhysicalToVirtualTime(_model.MarkerEnd) Then
                    virtStart = _model.PhysicalToVirtualTime(_model.MarkerStart)
                End If

                _playbackController.StopPlayback()
                _awaitingFirstValidTime = True
                If _tileRendererRef IsNot Nothing Then _tileRendererRef.IsMediaPlaying = True

                Dim audioForMuteSignal As String = String.Empty
                If _isAudioReplaced Then
                    audioForMuteSignal = _externalAudioPath
                End If

                SetVolumeFromTrackBar()

                SafeSetVideoViewPreviewImage(Nothing)

                Await _playbackController.PlayAsync(selectedFiles(0), virtStart, audioForMuteSignal)
                StartPlaybackUIUpdateLoop()
                UpdateLabel("Воспроизведение запущено")
            End If

            UpdateDynamicStatus(True)
            If PictureBox1 IsNot Nothing AndAlso Not PictureBox1.IsDisposed Then
                PushStateToRenderer()
            End If

        Catch ex As Exception
            SafeLog("Ошибка в ToolStripButton3_Click: " & ex.Message)
            UpdateLabel("Ошибка переключения режима")
        End Try
    End Sub

    Private Sub UpdateFileInfoLabel(videoPath As String, info As FFmpegService.MediaInfo)
        If isClosing OrElse ToolStripLabel1 Is Nothing Then Return

        Dim infoText As String = ""
        Dim bitrateStr As String = If(info.Bitrate = "2000" AndAlso info.Codec = "N/A", "Неизвестно", $"{info.Bitrate} kbps")

        Dim isVideo As Boolean = info.Width > 0 AndAlso info.Height > 0 AndAlso info.Fps > 0
        Dim isImage As Boolean = info.Width > 0 AndAlso info.Height > 0 AndAlso info.Fps = 0
        Dim isAudioOnly As Boolean = info.Width = 0 AndAlso info.Height = 0 AndAlso info.HasAudio

        If isAudioOnly Then
            infoText = $" Формат: {info.Codec} | {bitrateStr}"
        ElseIf isImage Then
            infoText = $" {info.Width}x{info.Height} | Формат: {info.Codec}"
        Else
            Dim resStr As String = If(info.Width > 0 AndAlso info.Height > 0, $"{info.Width}x{info.Height}", "N/A")
            Dim fpsStr As String = If(info.Fps > 0, info.Fps.ToString("F2", CultureInfo.InvariantCulture), "N/A")
            infoText = $" {resStr} | {info.Codec} | {fpsStr} fps | {bitrateStr}"
        End If

        SafeUIInvoke(Sub()
                         If Not ToolStripLabel1.IsDisposed Then
                             Dim parentToolStrip = ToolStripLabel1.GetCurrentParent()
                             If parentToolStrip?.IsDisposed = False Then
                                 ToolStripLabel1.Text = infoText
                             End If
                         End If
                     End Sub)
    End Sub

    Private Sub SetVolumeFromTrackBar()
        ApplyDynamicVolume(_currentVirtualPlaybackTime)
    End Sub

    Private Sub InitializeVolumeUI()
        If TrackBar1 Is Nothing Then Return
        TrackBar1.Minimum = 0
        TrackBar1.Maximum = 10
        TrackBar1.Value = 10
        TrackBar1.TickFrequency = 1
        TrackBar1.LargeChange = 1
        TrackBar1.SmallChange = 1

        _masterVolumeCache = Math.Max(0, Math.Min(100, TrackBar1.Value * 10))
        SetVolumeFromTrackBar()
    End Sub

    Private Sub TrackBar1_Scroll(sender As Object, e As EventArgs) Handles TrackBar1.Scroll
        If TrackBar1 IsNot Nothing Then _masterVolumeCache = Math.Max(0, Math.Min(100, TrackBar1.Value * 10))
        SetVolumeFromTrackBar()
    End Sub

    Private Sub TrackBar1_ValueChanged(sender As Object, e As EventArgs) Handles TrackBar1.ValueChanged
        If TrackBar1 IsNot Nothing Then _masterVolumeCache = Math.Max(0, Math.Min(100, TrackBar1.Value * 10))
        SetVolumeFromTrackBar()
    End Sub



    Private Sub ToolStripButton4_Click(sender As Object, e As EventArgs) Handles ToolStripButton4.Click
        If Not ToolStrip1.Enabled Then Return
        RaiseEvent ZoomInRequested(Me, EventArgs.Empty)
        _tileRendererRef?.SafeInvalidate()
    End Sub

    Private Sub ToolStripButton5_Click(sender As Object, e As EventArgs) Handles ToolStripButton5.Click
        If Not ToolStrip1.Enabled Then Return
        RaiseEvent ZoomOutRequested(Me, EventArgs.Empty)
        _tileRendererRef?.SafeInvalidate()
    End Sub

    Private Sub ToolStripButton6_Click(sender As Object, e As EventArgs) Handles ToolStripButton6.Click
        If selectedFiles Is Nothing OrElse selectedFiles.Count = 0 Then
            MessageBox.Show("Файл не загружен.", "Инфо", MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If

        If _model IsNot Nothing AndAlso _playbackController IsNot Nothing Then
            Dim splitTime As TimeSpan = _playbackController.CurrentVirtualTime
            _model.SplitClipAtTime(splitTime)

            If PictureBox1 IsNot Nothing AndAlso Not PictureBox1.IsDisposed Then
                PushStateToRenderer()
                PictureBox1.Invalidate()
            End If
            UpdateLabel("Инструмент Лезвие применен.")
        End If
    End Sub

    Private Sub ToolStripButton7_Click(sender As Object, e As EventArgs) Handles ToolStripButton7.Click
        If selectedFiles Is Nothing OrElse selectedFiles.Count = 0 Then
            MessageBox.Show("Файл не загружен.", "Инфо", MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If
        RaiseEvent ClearCutsRequested(Me, EventArgs.Empty)
    End Sub

    Private Async Sub ToolStripButton8_Click(sender As Object, e As EventArgs) Handles ToolStripButton8.Click
        If Not ToolStrip1.Enabled Then Return
        If isClosing OrElse selectedFiles Is Nothing OrElse selectedFiles.Count = 0 Then
            MessageBox.Show("Файл не загружен. Для замены аудио сначала откройте видеофайл.", "Инфо", MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If

        If _playbackController IsNot Nothing AndAlso _playbackController.State = IServices.PlaybackState.Playing Then
            MessageBox.Show("Замена аудио невозможна во время воспроизведения. Пожалуйста, поставьте видео на паузу.", "Инфо", MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If

        Using dlg As New OpenFileDialog With {
            .Title = "Выберите аудиофайл для замены",
            .Filter = "Аудиофайлы|*.mp3;*.wav;*.aac;*.flac;*.m4a;*.ogg|Все файлы (*.*)|*.*",
            .Multiselect = False
        }
            If dlg.ShowDialog() = DialogResult.OK Then
                DisableControls(True)
                Try
                    DeleteBakedAudioFile()
                    _externalAudioPath = dlg.FileName
                    _isAudioReplaced = True
                    _audioOffset = TimeSpan.Zero
                    _bakedAudioOffset = TimeSpan.Zero

                    Dim audioInfo = Await _ffmpegService.GetMediaInfoAsync(_externalAudioPath)


                    _previewCache.Clear()

                    _playbackController?.SetAudioOffset(TimeSpan.Zero)
                    _playbackController?.SetAudioDelay(0)

                    SetVolumeFromTrackBar()
                    UpdateLabel("Аудио заменено. Обновление аудиопотока...")
                    Await ExtractMasterWavIfNeededAsync()

                    Dim startSeg As TimeSpan = If(_model.IsZoomed, _model.ViewStart, TimeSpan.Zero)
                    Dim endSeg As TimeSpan = If(_model.IsZoomed, _model.ViewEnd, _model.TotalDuration)
                    Await UpdateTimelineAsync(selectedFiles(0), startSeg, endSeg, False)

                    EvaluateLogicRules()
                    UpdateDynamicStatus()
                    UpdateMarkerTimeLabel()

                    If Not isClosing AndAlso PictureBox1 IsNot Nothing AndAlso Not PictureBox1.IsDisposed Then
                        PushStateToRenderer()
                    End If
                    UpdateLabel("Замена аудио завершена. Готово")
                Catch ex As Exception
                    SafeLog("Ошибка при замене аудио (ToolStripButton8): " & ex.Message)
                    MessageBox.Show("Ошибка при замене аудио: " & ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error)
                    UpdateLabel("Ошибка при замене аудио")
                Finally
                    DisableControls(False)
                End Try
            End If
        End Using
    End Sub

    Private Async Sub ToolStripButton9_Click(sender As Object, e As EventArgs) Handles ToolStripButton9.Click
        If Not ToolStrip1.Enabled Then Return
        If isClosing OrElse selectedFiles Is Nothing OrElse selectedFiles.Count = 0 Then
            MessageBox.Show("Файл не загружен.", "Инфо", MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If

        If Not _isAudioReplaced Then
            MessageBox.Show("Аудио не было заменено внешним файлом. Возврат оригинального звука не требуется.", "Инфо", MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If

        If _playbackController IsNot Nothing AndAlso _playbackController.State = IServices.PlaybackState.Playing Then
            MessageBox.Show("Отмена замены аудио невозможна во время воспроизведения. Пожалуйста, поставьте видео на паузу.", "Инфо", MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If

        DisableControls(True)
        Try
            _externalAudioPath = String.Empty
            _isAudioReplaced = False
            _audioOffset = TimeSpan.Zero
            _bakedAudioOffset = TimeSpan.Zero


            _previewCache.Clear()

            _playbackController?.SetAudioOffset(TimeSpan.Zero)
            _playbackController?.SetAudioDelay(0)

            SetVolumeFromTrackBar()
            UpdateLabel("Замена аудио отменена. Возврат оригинального звука...")
            Await ExtractMasterWavIfNeededAsync()

            Dim startSeg As TimeSpan = If(_model.IsZoomed, _model.ViewStart, TimeSpan.Zero)
            Dim endSeg As TimeSpan = If(_model.IsZoomed, _model.ViewEnd, _model.TotalDuration)
            Await UpdateTimelineAsync(selectedFiles(0), startSeg, endSeg, False)

            EvaluateLogicRules()
            UpdateDynamicStatus()
            UpdateMarkerTimeLabel()

            If Not isClosing AndAlso PictureBox1 IsNot Nothing AndAlso Not PictureBox1.IsDisposed Then
                PushStateToRenderer()
            End If
            UpdateLabel("Возврат оригинального аудио завершен. Готово")
        Catch ex As Exception
            SafeLog("Ошибка при отмене замены аудио (ToolStripButton9): " & ex.Message)
            MessageBox.Show("Ошибка при отмене замены аудио: " & ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error)
            UpdateLabel("Ошибка при отмене замены аудио")
        Finally
            DisableControls(False)
        End Try
    End Sub

    Private Async Sub ToolStripButton10_Click(sender As Object, e As EventArgs) Handles ToolStripButton10.Click
        If Not ToolStrip1.Enabled Then Return
        If isClosing OrElse selectedFiles Is Nothing OrElse selectedFiles.Count = 0 Then
            MessageBox.Show("Файл не загружен.", "Инфо", MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If

        If _playbackController IsNot Nothing AndAlso _playbackController.State = IServices.PlaybackState.Playing Then
            MessageBox.Show("Сброс аудио параметров невозможен во время воспроизведения. Пожалуйста, поставьте видео на паузу.", "Инфо", MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If

        DisableControls(True)
        Try
            _isAudioReplaced = False
            _externalAudioPath = String.Empty
            _audioOffset = TimeSpan.Zero
            _bakedAudioOffset = TimeSpan.Zero

            DeleteBakedAudioFile()

            _previewCache.Clear()

            _playbackController?.SetAudioOffset(TimeSpan.Zero)
            _playbackController?.SetAudioDelay(0)
            _audioPlayer?.StopPlayback()
            SetVolumeFromTrackBar()

            UpdateLabel("Сброс параметров звука. Восстановление оригинальной дорожки...")
            Await ExtractMasterWavIfNeededAsync()

            Dim startSeg As TimeSpan = If(_model.IsZoomed, _model.ViewStart, TimeSpan.Zero)
            Dim endSeg As TimeSpan = If(_model.IsZoomed, _model.ViewEnd, TimeSpan.Zero)
            Await UpdateTimelineAsync(selectedFiles(0), startSeg, endSeg, False)

            EvaluateLogicRules()
            UpdateDynamicStatus()
            UpdateMarkerTimeLabel()

            If PictureBox1 IsNot Nothing AndAlso Not PictureBox1.IsDisposed Then
                PushStateToRenderer()
            End If
            UpdateLabel("Оригинальная аудиодорожка восстановлена, настройки звука сброшены")
        Catch ex As Exception
            SafeLog("Ошибка при возврате оригинального аудио (ToolStripButton10_Click): " & ex.Message)
            MessageBox.Show("Ошибка при возврате оригинального аудио: " & ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error)
            UpdateLabel("Ошибка при сбросе аудио")
        Finally
            DisableControls(False)
        End Try
    End Sub

    Private Sub НастройкиToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles НастройкиToolStripMenuItem.Click
        Form3.Show()
    End Sub

    Private Sub DeleteBakedAudioFile()
        If Not String.IsNullOrEmpty(_currentBakedAudioPath) AndAlso IO.File.Exists(_currentBakedAudioPath) Then
            Try
                IO.File.Delete(_currentBakedAudioPath)
            Catch ex As Exception
                SafeLog("Предупреждение: Не удалось удалить временный запеченный аудиофайл: " & ex.Message)
            End Try
        End If
    End Sub

    Private Async Function ProcessTextChangedAsync() As Task
        If isClosing Then Return
        _playbackController?.StopPlayback()

        If selectedFiles IsNot Nothing AndAlso selectedFiles.Count > 0 Then
            SaveMarkersForFile(selectedFiles(0))
        End If

        selectedFiles = FileManager.ParseInputFiles(TextBox1.Text)
        SafeSetVideoViewPreviewImage(Nothing)
        DeleteBakedAudioFile()
        DeleteProxyVideoFile()

        _audioOffset = TimeSpan.Zero
        _bakedAudioOffset = TimeSpan.Zero
        _isAudioReplaced = False
        _externalAudioPath = String.Empty
        _model.ClearAllClips()

        _playbackController?.SetAudioOffset(TimeSpan.Zero)
        Try
            _playbackController?.SetAudioDelay(0)
        Catch ex As Exception
            SafeLog("Ошибка сброса задержки аудиоплеера: " & ex.Message)
        End Try

        ClearAllGpuCaches()

        If _gpuFramePool IsNot Nothing Then
            _gpuFramePool.DisposeAll()
            _gpuFramePool = Nothing
        End If

        _asyncDecoder?.Dispose()
        _asyncDecoder = Nothing
        If _scrubCts IsNot Nothing Then
            _scrubCts.Cancel()
            _scrubCts.Dispose()
            _scrubCts = Nothing
        End If
        If _scrubLoopCts IsNot Nothing Then
            _scrubLoopCts.Cancel()
            _scrubLoopCts.Dispose()
            _scrubLoopCts = Nothing
        End If
        _directPlayer?.EndScrubbing()
        _lastScrubTime = DateTime.MinValue
        _wasPlayingBeforeScrub = False
        _currentFilePath = String.Empty

        Dim localHasImage As Boolean = False
        Dim localHasAudio As Boolean = False
        Dim localHasVideoWithAudio As Boolean = False
        Dim localHasVideoNoAudio As Boolean = False

        If selectedFiles IsNot Nothing AndAlso selectedFiles.Count > 0 Then
            Await Task.Run(Async Function() As Task
                               For Each file In selectedFiles
                                   Dim rawExt As String = Path.GetExtension(file)
                                   Dim ext As String = If(rawExt, String.Empty).ToLowerInvariant()

                                   If ImageExtensions.Contains(ext) Then
                                       localHasImage = True
                                   ElseIf AudioExtensions.Contains(ext) Then
                                       localHasAudio = True
                                   ElseIf VideoExtensions.Contains(ext) Then
                                       Dim fileInfo As FFmpegService.MediaInfo = Await _ffmpegService.GetMediaInfoAsync(file)
                                       If fileInfo.HasAudio Then
                                           localHasVideoWithAudio = True
                                       Else
                                           localHasVideoNoAudio = True
                                       End If
                                   End If
                               Next
                           End Function)

            inputHasImage = localHasImage
            inputHasAudio = localHasAudio
            inputHasVideoWithAudio = localHasVideoWithAudio
            inputHasVideoNoAudio = localHasVideoNoAudio

            Dim currentFile As String = selectedFiles(0)
            _currentFilePath = currentFile
            StartProLoading()

            Try
                Dim info As FFmpegService.MediaInfo = Await _ffmpegService.GetMediaInfoAsync(currentFile)
                _currentMediaInfo = info

                Dim isAudioOnlyClip As Boolean = (info.Width = 0 AndAlso info.Height = 0 AndAlso info.HasAudio)
                Dim newClip As New MediaClip() With {
                    .FilePath = currentFile,
                    .SourceDuration = info.Duration,
                    .SourceIn = TimeSpan.Zero,
                    .SourceOut = info.Duration,
                    .TimelineStart = TimeSpan.Zero,
                    .MediaType = If(isAudioOnlyClip, TargetFormatType.Audio, TargetFormatType.Video)
                }

                Dim targetTrackIdx As Integer = If(isAudioOnlyClip, 1, 0)
                _model.AddClipToTrack(targetTrackIdx, newClip)

                _currentFps = info.Fps
                If _currentFps <= 0 Then _currentFps = 30.0

                Await ExtractMasterWavIfNeededAsync()
                Await ExtractProxyVideoIfNeededAsync()

                SafeUIInvoke(Sub()
                                 UpdateComboBox4()

                                 If info.Width > 0 AndAlso info.Height > 0 AndAlso ComboBox5 IsNot Nothing Then
                                     Dim match As Form1.ResolutionProfile = ResolutionProfiles.FirstOrDefault(Function(p) p.Width = info.Width AndAlso p.Height = info.Height)
                                     If match IsNot Nothing Then
                                         ComboBox5.SelectedItem = match
                                     Else
                                         ComboBox5.SelectedIndex = 0
                                     End If
                                 End If
                             End Sub)

                LoadMarkersForFile(currentFile)
                Dim startSeg As TimeSpan = If(_model.IsZoomed, _model.ViewStart, TimeSpan.Zero)
                Dim endSeg As TimeSpan = If(_model.IsZoomed, _model.ViewEnd, _model.TotalDuration)

                Await UpdateTimelineAsync(currentFile, startSeg, endSeg, True)
                Await UpdatePreviewFrame(TimeSpan.Zero)
            Finally
                StopProLoading()
            End Try

        Else
            inputHasImage = False
            inputHasAudio = False
            inputHasVideoWithAudio = False
            inputHasVideoNoAudio = False
            _currentFilePath = String.Empty

            _model.SetMarkers(TimeSpan.Zero, TimeSpan.Zero)

            _model.ClearCuts()
            _model.ResetZoomHistory()

            If _tileRenderer IsNot Nothing Then
                _tileRenderer.SetDataSources(Nothing, Nothing)
                _tileRenderer.ClearStrips()
            End If

            If Not isClosing AndAlso PictureBox1 IsNot Nothing AndAlso Not PictureBox1.IsDisposed Then
                PushStateToRenderer()
            End If

            UpdateMarkerTimeLabel()

            If ToolStripLabel1?.IsDisposed = False Then
                Dim parentToolStrip = ToolStripLabel1.GetCurrentParent()
                If parentToolStrip?.IsDisposed = False Then
                    ToolStripLabel1.Text = "Файл не выбран"
                End If
            End If

            UpdateDynamicStatus()
        End If

        EvaluateLogicRules()
    End Function

    Private Async Function ExtractMasterWavIfNeededAsync() As Task(Of Boolean)
        If selectedFiles Is Nothing OrElse selectedFiles.Count = 0 Then
            Return False
        End If

        Dim currentFile As String = selectedFiles(0)
        StartProLoading()

        Try
            Dim info As FFmpegService.MediaInfo = Await _ffmpegService.GetMediaInfoAsync(currentFile)
            Dim effHasAudio As Boolean = info.HasAudio OrElse _isAudioReplaced

            If effHasAudio Then
                UpdateLabel("Инициализация потокового аудио...")

                Dim sourceForExt As String = currentFile
                If _isAudioReplaced AndAlso Not String.IsNullOrEmpty(_externalAudioPath) Then
                    sourceForExt = _externalAudioPath
                End If

                _audioPlayer.LoadStreaming(sourceForExt)
                Return True
            Else
                _audioPlayer.UnloadFile()
            End If

            Return False
        Finally
            StopProLoading()
        End Try
    End Function

    Private Function ResolveBdmvToM2ts(filePath As String) As String
        If Not IO.Path.GetExtension(filePath).Equals(".bdmv", StringComparison.OrdinalIgnoreCase) Then
            Return filePath
        End If

        Try
            Dim bdmvDir As String = IO.Path.GetDirectoryName(filePath)

            If IO.Path.GetFileName(bdmvDir).Equals("BACKUP", StringComparison.OrdinalIgnoreCase) Then
                bdmvDir = IO.Path.GetDirectoryName(bdmvDir)
            End If

            Dim streamDir As String = IO.Path.Combine(bdmvDir, "STREAM")
            If IO.Directory.Exists(streamDir) Then
                Dim m2tsFiles = IO.Directory.GetFiles(streamDir, "*.m2ts")
                If m2tsFiles.Length > 0 Then
                    Dim largestFile = m2tsFiles.OrderByDescending(Function(f) New IO.FileInfo(f).Length).First()
                    Return largestFile
                End If
            End If
        Catch ex As Exception
            System.Diagnostics.Debug.WriteLine($"[BDMV Resolver] Ошибка парсинга Blu-ray структуры: {ex.Message}")
        End Try

        Return Nothing
    End Function

    Public Function AskOverwrite(filePath As String) As Boolean Implements IMainEditorView.AskOverwrite
        Dim result As DialogResult = MessageBox.Show($"Файл '{filePath}' уже существует." & vbCrLf & "Вы хотите заменить его?", "Подтверждение замены", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
        Return result = DialogResult.Yes
    End Function

    Public Sub UpdateExportProgress(percentage As Integer, timeRemaining As String) Implements IMainEditorView.UpdateExportProgress
        SafeUIInvoke(Sub()
                         If ProgressBar1 IsNot Nothing AndAlso Not ProgressBar1.IsDisposed Then
                             ProgressBar1.Visible = True
                             ProgressBar1.Value = Math.Max(0, Math.Min(100, percentage))
                             ProgressBar1.Refresh()
                         End If
                         If Not String.IsNullOrEmpty(timeRemaining) Then
                             Label2.Text = $"Экспорт данных: {percentage}% | Осталось: {timeRemaining}"
                         End If
                     End Sub)
    End Sub

    Public Sub SetExportState(isExporting As Boolean) Implements IMainEditorView.SetExportState
        SafeUIInvoke(Sub()
                         SetUiExportState(Not isExporting)
                         If Not isExporting AndAlso ProgressBar1 IsNot Nothing Then
                             ProgressBar1.Refresh()
                         End If
                     End Sub)
    End Sub

    Private Class ModernToolStripRenderer
        Inherits ToolStripProfessionalRenderer

        Public Sub New()
            MyBase.New(New ModernColorTable())
            Me.RoundedEdges = False
        End Sub

        Protected Overrides Sub OnRenderItemText(e As ToolStripItemTextRenderEventArgs)
            Dim isDark As Boolean = ThemeManager.IsDarkTheme
            If e.Item.Enabled Then
                e.TextColor = If(isDark, Color.White, Color.FromArgb(20, 20, 20))
            Else
                e.TextColor = If(isDark, Color.FromArgb(120, 120, 120), Color.FromArgb(140, 140, 140))
            End If
            MyBase.OnRenderItemText(e)
        End Sub
    End Class

    Private Class ModernColorTable
        Inherits ProfessionalColorTable

        Private Shared ReadOnly Property IsDark As Boolean
            Get
                Return ThemeManager.IsDarkTheme
            End Get
        End Property

        Public Overrides ReadOnly Property ToolStripBorder As Color
            Get
                Return Color.Transparent
            End Get
        End Property

        Public Overrides ReadOnly Property MenuBorder As Color
            Get
                Return If(IsDark, Color.FromArgb(45, 45, 48), Color.FromArgb(200, 200, 200))
            End Get
        End Property

        Public Overrides ReadOnly Property MenuItemBorder As Color
            Get
                Return Color.Transparent
            End Get
        End Property

        Public Overrides ReadOnly Property MenuItemSelected As Color
            Get
                Return If(IsDark, Color.FromArgb(62, 62, 66), Color.FromArgb(220, 220, 220))
            End Get
        End Property

        Public Overrides ReadOnly Property MenuItemSelectedGradientBegin As Color
            Get
                Return MenuItemSelected
            End Get
        End Property

        Public Overrides ReadOnly Property MenuItemSelectedGradientEnd As Color
            Get
                Return MenuItemSelected
            End Get
        End Property

        Public Overrides ReadOnly Property MenuItemPressedGradientBegin As Color
            Get
                Return If(IsDark, Color.FromArgb(28, 28, 32), Color.FromArgb(200, 200, 200))
            End Get
        End Property

        Public Overrides ReadOnly Property MenuItemPressedGradientEnd As Color
            Get
                Return MenuItemPressedGradientBegin
            End Get
        End Property

        Public Overrides ReadOnly Property ToolStripDropDownBackground As Color
            Get
                Return If(IsDark, Color.FromArgb(28, 28, 32), Color.FromArgb(243, 243, 243))
            End Get
        End Property

        Public Overrides ReadOnly Property ImageMarginGradientBegin As Color
            Get
                Return ToolStripDropDownBackground
            End Get
        End Property

        Public Overrides ReadOnly Property ImageMarginGradientMiddle As Color
            Get
                Return ToolStripDropDownBackground
            End Get
        End Property

        Public Overrides ReadOnly Property ImageMarginGradientEnd As Color
            Get
                Return ToolStripDropDownBackground
            End Get
        End Property

        Public Overrides ReadOnly Property ButtonSelectedHighlight As Color
            Get
                Return MenuItemSelected
            End Get
        End Property

        Public Overrides ReadOnly Property ButtonSelectedHighlightBorder As Color
            Get
                Return If(IsDark, Color.FromArgb(80, 80, 85), Color.FromArgb(150, 150, 150))
            End Get
        End Property

        Public Overrides ReadOnly Property ButtonCheckedHighlightBorder As Color
            Get
                Return If(IsDark, Color.FromArgb(100, 100, 105), Color.FromArgb(150, 150, 150))
            End Get
        End Property

        Public Overrides ReadOnly Property ButtonPressedHighlight As Color
            Get
                Return MenuItemPressedGradientBegin
            End Get
        End Property

        Public Overrides ReadOnly Property ButtonPressedHighlightBorder As Color
            Get
                Return If(IsDark, Color.FromArgb(100, 100, 105), Color.FromArgb(130, 130, 130))
            End Get
        End Property
    End Class

    Private Sub ApplyDynamicVolume(virtualTime As TimeSpan)
        If _playbackController Is Nothing OrElse _tileRendererRef Is Nothing OrElse _model Is Nothing Then Return

        Dim masterVolume As Integer = _masterVolumeCache
        Dim trackVol As Single = _trackVolume

        Dim fadeIn As TimeSpan = _tileRendererRef.AudioFadeIn
        Dim fadeOut As TimeSpan = _tileRendererRef.AudioFadeOut
        Dim virtStart As TimeSpan = _model.PhysicalToVirtualTime(_model.MarkerStart)
        Dim virtEnd As TimeSpan = _model.PhysicalToVirtualTime(_model.MarkerEnd)

        Dim fadeMultiplier As Single = 1.0F

        If fadeIn > TimeSpan.Zero AndAlso virtualTime >= virtStart AndAlso virtualTime < (virtStart + fadeIn) Then
            fadeMultiplier = CSng((virtualTime - virtStart).TotalSeconds / fadeIn.TotalSeconds)
        End If

        If fadeOut > TimeSpan.Zero AndAlso virtualTime <= virtEnd AndAlso virtualTime > (virtEnd - fadeOut) Then
            Dim outMult As Single = CSng((virtEnd - virtualTime).TotalSeconds / fadeOut.TotalSeconds)
            fadeMultiplier = Math.Min(fadeMultiplier, outMult)
        End If

        If fadeMultiplier < 0.0F Then fadeMultiplier = 0.0F
        If fadeMultiplier > 1.0F Then fadeMultiplier = 1.0F

        Dim finalVolume As Integer = CInt(Math.Round(masterVolume * trackVol * fadeMultiplier))

        _playbackController.SetVolume(finalVolume)
    End Sub

    Private Function CalculateTargetDuration(hasCuts As Boolean) As Double
        Dim targetDurationSec As Double = 0

        If hasCuts Then
            Dim currentStart As TimeSpan = _model.MarkerStart

            For Each cut In _model.Cuts.OrderBy(Function(c) c.StartTime)
                If cut.EndTime <= _model.MarkerStart Then Continue For
                If cut.StartTime >= _model.MarkerEnd Then Continue For

                Dim effCutStart As TimeSpan = If(cut.StartTime < _model.MarkerStart, _model.MarkerStart, cut.StartTime)
                Dim effCutEnd As TimeSpan = If(cut.EndTime > _model.MarkerEnd, _model.MarkerEnd, cut.EndTime)

                If effCutStart > currentStart Then
                    targetDurationSec += (effCutStart - currentStart).TotalSeconds
                End If

                If effCutEnd > currentStart Then
                    currentStart = effCutEnd
                End If
            Next

            If currentStart < _model.MarkerEnd Then
                targetDurationSec += (_model.MarkerEnd - currentStart).TotalSeconds
            End If
        Else
            targetDurationSec = (_model.MarkerEnd - _model.MarkerStart).TotalSeconds
        End If

        Return targetDurationSec
    End Function

    Public Sub ShowLoadingState(message As String) Implements IMainEditorView.ShowLoadingState
        SafeUIInvoke(Sub()
                         Label2.Text = message
                         StartProLoading()
                         DisableControls(True)
                     End Sub)
    End Sub

    Public Sub HideLoadingState() Implements IMainEditorView.HideLoadingState
        SafeUIInvoke(Sub()
                         StopProLoading()
                         DisableControls(False)
                         Label2.Text = "Готов к работе"
                         EvaluateLogicRules()
                     End Sub)
    End Sub

    Public Sub SetPreviewImage(bmp As Bitmap) Implements IMainEditorView.SetPreviewImage
        SafeSetVideoViewPreviewImage(bmp)
    End Sub

    Public Sub UpdateMediaInfoUI(infoText As String, hasMedia As Boolean) Implements IMainEditorView.UpdateMediaInfoUI
        SafeUIInvoke(Sub()
                         If ToolStripLabel1 IsNot Nothing AndAlso Not ToolStripLabel1.IsDisposed Then
                             ToolStripLabel1.Text = infoText
                         End If

                         If Not hasMedia Then
                             inputHasImage = False
                             inputHasAudio = False
                             inputHasVideoWithAudio = False
                             inputHasVideoNoAudio = False
                         End If
                     End Sub)
    End Sub

    Public Sub UpdateResolutionProfiles(width As Integer, height As Integer) Implements IMainEditorView.UpdateResolutionProfiles
        SafeUIInvoke(Sub()
                         UpdateComboBox4()

                         If width > 0 AndAlso height > 0 AndAlso ComboBox5 IsNot Nothing Then
                             Dim match As Form1.ResolutionProfile = ResolutionProfiles.FirstOrDefault(Function(p) p.Width = width AndAlso p.Height = height)
                             If match IsNot Nothing Then
                                 ComboBox5.SelectedItem = match
                             Else
                                 ComboBox5.SelectedIndex = 0
                             End If
                         End If
                     End Sub)
    End Sub

    Public Sub SetHardwareControlsState(isAudioOnly As Boolean) Implements IMainEditorView.SetHardwareControlsState
        SafeUIInvoke(Sub()
                         If isAudioOnly Then
                             If ComboBox2 IsNot Nothing Then
                                 Dim cpuIndex As Integer = -1
                                 For i As Integer = 0 To ComboBox2.Items.Count - 1
                                     If ComboBox2.Items(i).ToString().Contains("CPU", StringComparison.OrdinalIgnoreCase) Then
                                         cpuIndex = i
                                         Exit For
                                     End If
                                 Next
                                 If cpuIndex >= 0 Then ComboBox2.SelectedIndex = cpuIndex
                             End If
                             Label2.Text = "Для аудио используется только процессор (CPU)."
                         End If
                     End Sub)
    End Sub

    Private Async Sub ОчиститьБуферToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles ОчиститьБуферToolStripMenuItem.Click
        If isClosing Then Return
        SafeUIInvoke(Sub()
                         If ОчиститьБуферToolStripMenuItem IsNot Nothing Then ОчиститьБуферToolStripMenuItem.Enabled = False
                         UpdateLabel("Глубокая очистка: остановка процессов и удаление всех кэшей...")
                     End Sub)

        Try
            _playbackController?.StopPlayback()
            SafeCancelAndDisposeCTS(generateContactCts, generateLock)
            ClearAllGpuCaches()

            _previewCache.Clear()

            DeleteBakedAudioFile()
            DeleteProxyVideoFile()

            _fileManager?.ClearAllTemporaryFiles()

            Await Task.Run(Sub()
                               GC.Collect()
                               GC.WaitForPendingFinalizers()
                           End Sub)

            If selectedFiles IsNot Nothing AndAlso selectedFiles.Count > 0 Then
                SafeUIInvoke(Sub() UpdateLabel("Пересборка таймлайна начисто..."))
                Dim startSeg As TimeSpan = If(_model.IsZoomed, _model.ViewStart, TimeSpan.Zero)
                Dim endSeg As TimeSpan = If(_model.IsZoomed, _model.ViewEnd, _model.TotalDuration)

                Await UpdateTimelineAsync(selectedFiles(0), startSeg, endSeg, False)
                Await UpdatePreviewFrame(_lastPreviewTime, True)
            End If

            SafeUIInvoke(Sub()
                             If PictureBox1 IsNot Nothing AndAlso Not PictureBox1.IsDisposed Then
                                 PushStateToRenderer()
                                 PictureBox1.Invalidate()
                             End If
                         End Sub)

        Catch ex As Exception
            SafeLog("Ошибка при нажатии на ОчиститьБуферToolStripMenuItem (Глубокая очистка): " & ex.Message)
            MessageBox.Show("Произошла ошибка при очистке: " & ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error)
        Finally
            SafeUIInvoke(Sub()
                             UpdateLabel("Все кэши (ОЗУ, Диск, VRAM) успешно очищены")
                             If ОчиститьБуферToolStripMenuItem IsNot Nothing Then ОчиститьБуферToolStripMenuItem.Enabled = True
                         End Sub)
        End Try
    End Sub

    Private Sub ВидеоФайлыToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles ВидеоФайлыToolStripMenuItem.Click
        Dim warningMsg As String = String.Empty
        Dim dir As String = _fileManager.GetDownloadsDirectory(warningMsg)

        If Directory.Exists(dir) Then
            Try
                Process.Start("explorer.exe", """" & dir & """")
            Catch ex As Exception
                MessageBox.Show("Не удалось открыть папку: " & ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error)
            End Try
        Else
            MessageBox.Show("Папка не существует: " & dir, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End If
    End Sub

    Private Async Sub ОткрытьToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles ОткрытьToolStripMenuItem.Click
        Using dlg As New OpenFileDialog With {
            .Multiselect = True,
            .Filter = "Медиафайлы|*.mp4;*.webm;*.mkv;*.avi;*.mov;*.flv;*.ts;*.m2ts;*.bdmv;*.wmv;*.mp3;*.wav;*.aac;*.flac;*.webp;*.gif;*.png;*.jpg;*.jpeg;*.jxl|Все файлы (*.*)|*.*"
        }
            If dlg.ShowDialog() = DialogResult.OK Then
                Dim resolvedFiles As New List(Of String)()
                For Each file In dlg.FileNames
                    Dim actualFilePath As String = ResolveBdmvToM2ts(file)
                    If Not String.IsNullOrEmpty(actualFilePath) Then resolvedFiles.Add(actualFilePath)
                Next

                If resolvedFiles.Count > 0 Then
                    ' ИСПРАВЛЕНИЕ: Мы жестко передаем False, 
                    ' чтобы программа всегда удаляла старые файлы и открывала новые
                    Await ImportMediaFilesAsync(resolvedFiles, False)
                End If
            End If
        End Using
    End Sub

    Private Async Function ImportMediaFilesAsync(newFiles As List(Of String), append As Boolean) As Task
        If isClosing Then Return
        _playbackController?.StopPlayback()
        StartProLoading()
        DisableControls(True)

        Try
            Dim insertTime As TimeSpan = TimeSpan.Zero
            If Not append Then
                _model.ClearAllClips()
                selectedFiles.Clear()
                _audioOffset = TimeSpan.Zero
                _bakedAudioOffset = TimeSpan.Zero
                _isAudioReplaced = False
                _externalAudioPath = String.Empty
            Else
                insertTime = _model.TotalDuration
            End If

            Dim hasNewVideo As Boolean = False
            Dim firstVideoFile As String = ""

            For Each filePath In newFiles
                If Not selectedFiles.Contains(filePath) Then
                    selectedFiles.Add(filePath)
                End If

                Dim info As FFmpegService.MediaInfo = Await _ffmpegService.GetMediaInfoAsync(filePath)
                Dim isAudioOnly As Boolean = (info.Width = 0 AndAlso info.Height = 0 AndAlso info.HasAudio)

                ' 1. ДОБАВЛЯЕМ ВИДЕО (если есть)
                If Not isAudioOnly Then
                    Dim vClip As New MediaClip() With {
                        .Id = Guid.NewGuid(),
                        .FilePath = filePath,
                        .SourceDuration = info.Duration,
                        .SourceIn = TimeSpan.Zero,
                        .SourceOut = info.Duration,
                        .TimelineStart = insertTime,
                        .MediaType = TargetFormatType.Video,
                        .Scale = 1.0F,
                        .Volume = 1.0F
                    }
                    _model.AddClipSequential(vClip)

                    If Not hasNewVideo Then
                        hasNewVideo = True
                        firstVideoFile = filePath
                        _currentMediaInfo = info
                        _currentFps = If(info.Fps > 0, info.Fps, 30.0)
                        _currentFilePath = filePath
                    End If
                End If

                ' 2. ДОБАВЛЯЕМ АУДИО (если есть звук у видео ИЛИ это просто аудиофайл)
                If info.HasAudio Then
                    Dim aClip As New MediaClip() With {
                        .Id = Guid.NewGuid(),
                        .FilePath = filePath,
                        .SourceDuration = info.Duration,
                        .SourceIn = TimeSpan.Zero,
                        .SourceOut = info.Duration,
                        .TimelineStart = insertTime,
                        .MediaType = TargetFormatType.Audio,
                        .Scale = 1.0F,
                        .Volume = 1.0F
                    }
                    _model.AddClipSequential(aClip)
                    inputHasAudio = True
                    If Not isAudioOnly Then inputHasVideoWithAudio = True
                ElseIf Not isAudioOnly Then
                    inputHasVideoNoAudio = True
                End If
            Next

            If hasNewVideo Then
                UpdateFileInfoLabel(firstVideoFile, _currentMediaInfo)
            End If

            _previewCache.Clear()

            Dim startSeg As TimeSpan = If(_model.IsZoomed, _model.ViewStart, TimeSpan.Zero)
            Dim endSeg As TimeSpan = If(_model.IsZoomed, _model.ViewEnd, _model.TotalDuration)

            If Not append Then
                _model.SetMarkers(TimeSpan.Zero, _model.TotalDuration)
                _model.ResetZoomHistory()
                startSeg = TimeSpan.Zero
                endSeg = _model.TotalDuration
            Else
                If _model.MarkerEnd < _model.TotalDuration Then
                    _model.SetMarkers(_model.MarkerStart, _model.TotalDuration)
                    endSeg = _model.TotalDuration
                End If
            End If

            Await UpdateTimelineAsync(If(hasNewVideo, firstVideoFile, selectedFiles(0)), startSeg, endSeg, Not append)
            Await UpdatePreviewFrame(_model.MarkerStart, True)

            RemoveHandler TextBox1.TextChanged, AddressOf TextBox1_TextChanged
            TextBox1.Text = String.Join(vbCrLf, selectedFiles)
            AddHandler TextBox1.TextChanged, AddressOf TextBox1_TextChanged

            EvaluateLogicRules()
            UpdateDynamicStatus(True)
            UpdateMarkerTimeLabel()

        Catch ex As Exception
            SafeLog("Ошибка импорта: " & ex.Message)
            MessageBox.Show("Ошибка при импорте файлов: " & ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error)
        Finally
            StopProLoading()
            DisableControls(False)
        End Try
    End Function

    Private Sub УдалитьToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles УдалитьToolStripMenuItem.Click
        If _model IsNot Nothing AndAlso _tileRendererRef IsNot Nothing Then
            Dim clipToDelete = _tileRendererRef.SelectedClip

            If clipToDelete IsNot Nothing Then
                _model.RemoveClip(clipToDelete.Id)
                _tileRendererRef.SelectedClip = Nothing

                If PictureBox1 IsNot Nothing AndAlso Not PictureBox1.IsDisposed Then
                    PushStateToRenderer()
                    PictureBox1.Invalidate()
                End If
                UpdateLabel("Клип удален.")
            Else
                MessageBox.Show("Сначала выделите клип (кликнув по нему левой кнопкой мыши), чтобы его удалить.", "Информация", MessageBoxButtons.OK, MessageBoxIcon.Information)
            End If
        End If
    End Sub

    Private Sub ВырезатьToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles ВырезатьToolStripMenuItem.Click
        If _model IsNot Nothing AndAlso _playbackController IsNot Nothing AndAlso selectedFiles.Count > 0 Then
            Dim splitTime As TimeSpan = _playbackController.CurrentVirtualTime
            _model.SplitClipAtTime(splitTime)

            If PictureBox1 IsNot Nothing AndAlso Not PictureBox1.IsDisposed Then
                PushStateToRenderer()
                PictureBox1.Invalidate()
            End If
            UpdateLabel("Клип разрезан (по положению курсора).")
        End If
    End Sub

    Private Sub ОтменаToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles ОтменаToolStripMenuItem.Click
        If _model IsNot Nothing Then
            _model.Undo()
            If PictureBox1 IsNot Nothing AndAlso Not PictureBox1.IsDisposed Then
                PushStateToRenderer()
                PictureBox1.Invalidate()
            End If
            UpdateLabel("Действие отменено.")
        End If
    End Sub



    Private Function GetTrackIndexForClip(clipId As Guid) As Integer
        If _model Is Nothing Then Return -1
        For i As Integer = 0 To _model.Tracks.Count - 1
            If _model.Tracks(i).Clips.Any(Function(c) c.Id = clipId) Then Return i
        Next
        Return -1
    End Function


    Private Sub Renderer_SelectionChanged(clip As MediaClip)
        If Me.InvokeRequired Then
            Me.BeginInvoke(New Action(Sub() Renderer_SelectionChanged(clip)))
            Return
        End If

        ' Если форма инспектора еще не создана или закрыта, ничего не делаем
        If _inspectorForm Is Nothing OrElse _inspectorForm.IsDisposed Then Return

        If clip Is Nothing OrElse clip.MediaType = TargetFormatType.Audio Then
            _inspectorForm.Enabled = False
        Else
            _inspectorForm.Enabled = True
            _inspectorForm.LoadClipData(clip)

            ' По желанию: если хотите, чтобы панель автоматически открывалась при клике по видео:
            ' If Not _inspectorForm.Visible Then _inspectorForm.Show(Me)
        End If
    End Sub

    ' Реализация кнопок меню из вашего запроса:
    Private Sub ДобавитьВидеотрекToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles ДобавитьВидеотрекToolStripMenuItem.Click
        If _model IsNot Nothing Then
            ' Используем Where для фильтрации, а затем Count()
            Dim vCount = _model.Tracks.Where(Function(t) t.Type = TargetFormatType.Video).Count()
            _model.AddTrack(TargetFormatType.Video, "Video " & (vCount + 1))
            UpdateTimelineUI()
            UpdateLabel("Добавлен новый видеотрек")
        End If
    End Sub

    Private Sub ДобавитьАудиотрекToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles ДобавитьАудиотрекToolStripMenuItem.Click
        If _model IsNot Nothing Then
            ' Используем Where для фильтрации, а затем Count()
            Dim aCount = _model.Tracks.Where(Function(t) t.Type = TargetFormatType.Audio).Count()
            _model.AddTrack(TargetFormatType.Audio, "Audio " & (aCount + 1))
            UpdateTimelineUI()
            UpdateLabel("Добавлен новый аудиотрек")
        End If
    End Sub

    Private Sub УдалитьПоследнийТрекToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles УдалитьПоследнийТрекToolStripMenuItem.Click
        If _model IsNot Nothing Then
            _model.RemoveLastTrack()
            UpdateTimelineUI()
            UpdateLabel("Трек удален")
        End If
    End Sub

    Private Sub ОбновлениеИнтерфейсаToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles ОбновлениеИнтерфейсаToolStripMenuItem.Click
        UpdateTimelineUI()
        UpdateLabel("Интерфейс обновлен")
    End Sub

    Private Sub UpdateTimelineUI()
        If PictureBox1 IsNot Nothing AndAlso Not PictureBox1.IsDisposed Then
            _tileRenderer?.Resize(PictureBox1.ClientSize.Width, PictureBox1.ClientSize.Height)
            PushStateToRenderer()
            PictureBox1.Invalidate()
        End If
    End Sub

    Private Sub ПанельВидеоToolStripMenuItem_Click(sender As Object, e As EventArgs) Handles ПанельВидеоToolStripMenuItem.Click
        ' Ленивая инициализация: создаем форму только при первом вызове
        If _inspectorForm Is Nothing OrElse _inspectorForm.IsDisposed Then
            ' Метод, который будет дергать ползунок из Form4 для перерисовки видео
            _inspectorForm = New Form4 With {
                .Model = _model,
                .DirectPlayer = _directPlayer,
                .TileRendererRef = _tileRendererRef,
                .ActionPushState = AddressOf PushStateToRenderer,
                .ActionForceRealtimeUpdate = Sub()
                                                 _directPlayer?.RefreshComposition()
                                                 If _playbackController IsNot Nothing AndAlso Not _playbackController.IsPlaying Then
                                                     ' 1. Прячем картинку-заглушку, открывая 3D-рендер
                                                     If InternalPreviewBox IsNot Nothing AndAlso InternalPreviewBox.Visible Then
                                                         InternalPreviewBox.Visible = False
                                                     End If

                                                     ' 2. Сбрасываем кэш таймера, заставляя немедленно отрендерить новый кадр
                                                     _lastDecodedScrubTime = TimeSpan.MinValue
                                                     _scrubTargetTime = _currentVirtualPlaybackTime
                                                     If Not _isScrubLoopRunning Then StartScrubLoop()
                                                 End If
                                             End Sub
            }
        End If

        ' Переключатель (Toggle) видимости окна
        If _inspectorForm.Visible Then
            _inspectorForm.Hide()
        Else
            _inspectorForm.Show(Me) ' Показываем поверх основного окна (Owned Form)

            ' Если мы её открыли, проверяем, активна ли она для текущего выделенного клипа
            Dim currentClip = _tileRendererRef?.SelectedClip
            If currentClip IsNot Nothing AndAlso currentClip.MediaType <> TargetFormatType.Audio Then
                _inspectorForm.Enabled = True
                _inspectorForm.LoadClipData(currentClip)
            Else
                _inspectorForm.Enabled = False
            End If
        End If
    End Sub

End Class