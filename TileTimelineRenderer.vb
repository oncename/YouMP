Option Strict On
Option Explicit On

Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Linq
Imports System.Runtime.InteropServices
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports SharpDX
Imports SharpDX.Direct2D1
Imports SharpDX.Mathematics.Interop
Imports yoump.IServices

Imports Direct2D = SharpDX.Direct2D1
Imports DirectWrite = SharpDX.DirectWrite
Imports DXGI = SharpDX.DXGI
Imports Bitmap = SharpDX.Direct2D1.Bitmap
Imports D3D11 = SharpDX.Direct3D11
Imports D3D = SharpDX.Direct3D

Public Class TileTimelineRenderer
    Implements ITimelineRenderer, IDisposable

    ' ==========================================================
    ' СВЯЗЬ С PROJECT MODEL (Единый источник истины)
    ' ==========================================================
    Private _projectModel As ProjectModel

    Public Property ProjectModel As ProjectModel
        Get
            Return _projectModel
        End Get
        Set(value As ProjectModel)
            _projectModel = value
            SafeInvalidate()
        End Set
    End Property

    Private Class RenderThumbnail
        Implements IDisposable
        Public D3DTexture As D3D11.Texture2D
        Public D2DBitmap As Direct2D.Bitmap
        Public PtsMs As Double

        Public Sub Dispose() Implements IDisposable.Dispose
            If D2DBitmap IsNot Nothing Then D2DBitmap.Dispose() : D2DBitmap = Nothing
            If D3DTexture IsNot Nothing Then D3DTexture.Dispose() : D3DTexture = Nothing
        End Sub
    End Class

    Private ReadOnly _thumbnails As New Dictionary(Of String, RenderThumbnail)()

    Public Enum VegasFadeType
        Linear = 0
        Fast = 1
        Slow = 2
        Smooth = 3
        Sharp = 4
    End Enum

    Private Enum TimelineInteractionState
        Idle
        ScrubbingPlayhead
        DraggingStartMarker
        DraggingEndMarker
        AudioShifting
        Slipping
        DraggingFadeIn
        DraggingFadeOut
        DraggingVideoFadeIn
        DraggingVideoFadeOut
        DraggingVideoFadeInCurve
        DraggingVideoFadeOutCurve
        MovingClip
        TrimmingClipLeft
        TrimmingClipRight
    End Enum

    Private _selectedClip As MediaClip = Nothing
    Private _selectedTrack As IServices.TrackSnapshot = Nothing
    Private _dragStartTimelineTime As TimeSpan
    Private _originalTimelineStart As TimeSpan
    Private _originalSourceIn As TimeSpan
    Private _originalSourceOut As TimeSpan

    Private _audioFadeIn As TimeSpan = TimeSpan.Zero
    Private _audioFadeOut As TimeSpan = TimeSpan.Zero
    Private _trackVolumeValue As Single = 1.0F

    Private ReadOnly _activeFrameTasks As New System.Collections.Concurrent.ConcurrentDictionary(Of String, Boolean)()
    Private ReadOnly _cancellationTokens As New System.Collections.Concurrent.ConcurrentDictionary(Of String, CancellationTokenSource)()

    Private _videoFadeIn As TimeSpan = TimeSpan.Zero
    Private _videoFadeOut As TimeSpan = TimeSpan.Zero
    Public Property VideoFadeInType As VegasFadeType = VegasFadeType.Smooth
    Public Property VideoFadeOutType As VegasFadeType = VegasFadeType.Smooth
    Private ReadOnly _curveDragStartY As Integer
    Private ReadOnly _initialCurveType As Integer
    Private _isMediaPlaying As Boolean = False

    Private _clipSnapshotBeforeDrag As MediaClip = Nothing
    Private _trackIdxBeforeDrag As Integer = -1

    ' Линия прилипания
    Private _activeSnapTime As TimeSpan? = Nothing

    Public Enum ThumbRenderMode
        Standard = 0
        Minimal = 1
    End Enum

    Private _timelineThumbMode As Integer = 0

    Public Property TimelineThumbMode As Integer
        Get
            Return _timelineThumbMode
        End Get
        Set(value As Integer)
            If _timelineThumbMode <> value Then
                _timelineThumbMode = value
                SafeInvalidate() ' При изменении режима принудительно обновляем холст DirectX
            End If
        End Set
    End Property

    Public Property IsMediaPlaying As Boolean
        Get
            Return _isMediaPlaying
        End Get
        Set(value As Boolean)
            If _isMediaPlaying <> value Then
                _isMediaPlaying = value
                SafeInvalidate()
            End If
        End Set
    End Property

    Public Property AudioFadeIn As TimeSpan Implements ITimelineRenderer.AudioFadeIn
        Get
            Return _audioFadeIn
        End Get
        Set(value As TimeSpan)
            _audioFadeIn = value
            SafeInvalidate()
        End Set
    End Property

    Public Property AudioFadeOut As TimeSpan Implements ITimelineRenderer.AudioFadeOut
        Get
            Return _audioFadeOut
        End Get
        Set(value As TimeSpan)
            _audioFadeOut = value
            SafeInvalidate()
        End Set
    End Property

    Public Property VideoFadeIn As TimeSpan
        Get
            Return _videoFadeIn
        End Get
        Set(value As TimeSpan)
            _videoFadeIn = value
            SafeInvalidate()
        End Set
    End Property

    Public Property VideoFadeOut As TimeSpan
        Get
            Return _videoFadeOut
        End Get
        Set(value As TimeSpan)
            _videoFadeOut = value
            SafeInvalidate()
        End Set
    End Property

    Public Property TrackVolume As Single Implements ITimelineRenderer.TrackVolume
        Get
            Return _trackVolumeValue
        End Get
        Set(value As Single)
            If value < 0.0F Then value = 0.0F
            If _trackVolumeValue <> value Then
                _trackVolumeValue = value
                RaiseEvent TrackVolumeChanged(value)
                SafeInvalidate()
            End If
        End Set
    End Property

    Public ReadOnly Property Device As D3D11.Device
        Get
            Return _d3dDevice
        End Get
    End Property

    Public Property SelectedClip As MediaClip
        Get
            Return _selectedClip
        End Get
        Set(value As MediaClip)
            _selectedClip = value
            SafeInvalidate()
        End Set
    End Property

    Public Event AudioFadesChanged As Action(Of TimeSpan, TimeSpan)
    Public Event TrackVolumeChanged As Action(Of Single)
    Public Event SelectionChanged As Action(Of MediaClip)

    Private Const Padding As Integer = 0
    Private Const MarkerHitZone As Integer = 10
    Public Property ThumbSpacing As Single = 0.0F
    Public Property ThumbCount As Integer = 8
    Public Property FrameInterpolation As Direct2D.BitmapInterpolationMode = Direct2D.BitmapInterpolationMode.Linear

    Private _isDarkTheme As Boolean = True
    Public Property IsDarkTheme As Boolean Implements ITimelineRenderer.IsDarkTheme
        Get
            Return _isDarkTheme
        End Get
        Set(value As Boolean)
            _isDarkTheme = value
        End Set
    End Property

    Private _scrollOffset As Double = 0.0
    Private _totalTimelineWidth As Double = 0.0
    Private _zoomLevel As Double = 1.0
    Private _contentHeightWithoutScroll As Single
    Private _isScrollbarVisible As Boolean

    Private Const ScrollbarHeight As Single = 14.0F
    Private Const ScrollbarMinThumbWidth As Single = 30.0F
    Private Const ScrollbarPadding As Single = 2.0F
    Private _isDraggingScrollbar As Boolean = False
    Private ReadOnly _scrollDragStartX As Single = 0.0F
    Private ReadOnly _scrollDragStartOffset As Double = 0.0

    Private _tileSize As Drawing.Size

    Private ReadOnly _caches As New Dictionary(Of String, GpuFrameCacheManager)()
    Private ReadOnly _extractors As New Dictionary(Of String, GpuFrameExtractor)()

    Private ReadOnly _renderLock As New Object()
    Private _isDeviceValid As Boolean = False
    Private _boundControl As Control

    Private ReadOnly _strips As New Dictionary(Of Integer, Bitmap)()
    Private _previewBitmap As Bitmap = Nothing

    Private _audioPeaks As IServices.PeakMinMax() = Nothing
    Private _peaksPerSecond As Double = 187.5
    Private _brushAudioWaveformL As SolidColorBrush
    Private _brushAudioWaveformR As SolidColorBrush

    Private ReadOnly _audioPeaksCache As New Dictionary(Of String, IServices.PeakMinMax())()
    Private ReadOnly _lastCutsHash As Integer = 0

    Private _audioGradientStops As GradientStopCollection = Nothing
    Private _audioGradientBrush As LinearGradientBrush = Nothing
    Private _cachedGradientTop As Single = -1.0F
    Private _cachedGradientBottom As Single = -1.0F

    Private _d3dDevice As D3D11.Device
    Private _swapChain As DXGI.SwapChain
    Private _renderTarget As Direct2D.RenderTarget

    Private _brushBg, _brushText, _brushOutline, _brushPlaceholder As SolidColorBrush
    Private _brushTickLong, _brushTickShort, _brushCursor, _brushTooltipBg, _brushTooltipBorder As SolidColorBrush
    Private _brushLoadingBg, _brushAudioBg, _brushAudioCenterLine, _brushAudioSeparator, _brushAudioBorder As SolidColorBrush
    Private _brushAudioLabelBg, _brushAudioLabelBorder, _brushAudioLabelText As SolidColorBrush
    Private _brushPlayheadBorder, _brushPlayheadShadow, _brushPlayheadLine, _brushPlayheadFill As SolidColorBrush
    Private _brushMarkerBorder, _brushMarkerLine, _brushMarkerFill, _brushMarkerShadow, _brushMarkerAccent As SolidColorBrush
    Private _brushSplice, _brushCutRegion, _brushGlow, _brushMain, _brushSelection, _brushMarkerStart, _brushMarkerEnd, _brushPlaybackLine As SolidColorBrush
    Private _brushLine As SolidColorBrush
    Private _brushDim, _brushFadeLine, _brushFadeHandle, _brushMask As SolidColorBrush
    Private _factory As Direct2D.Factory
    Private _dwFactory As DirectWrite.Factory
    Private ReadOnly IdentityMatrix As New RawMatrix3x2(1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F)

    Private _startMarkerGeom, _endMarkerGeom, _loadingArcGeom As PathGeometry

    Private _state As New TimelineStateData() With {.CutRegions = New List(Of CutRegionData)()}
    Private _currentWidth As Single
    Private _currentHeight As Single
    Private _contentWidth As Single
    Private _timelineHeight As Single

    Private _fps As Double = 30.0
    Private _hasSelection As Boolean = False
    Private _isAudioReplaced As Boolean = False
    Private _hasAudio As Boolean = False

    Private _playheadTime As TimeSpan = TimeSpan.Zero
    Private _currentPlaybackX As Integer = -1

    Private _audioOffset As TimeSpan = TimeSpan.Zero
    Private _bakedAudioOffset As TimeSpan = TimeSpan.Zero

    Private ReadOnly _audioDragStartMouseX As Integer = 0
    Private ReadOnly _audioDragStartOffset As TimeSpan = TimeSpan.Zero

    Private _currentMouseX As Integer = -1
    Private _currentMouseY As Integer = -1
    Private _hoverTimeStr As String = String.Empty

    Private _disposed As Boolean = False
    Private _isLoading As Boolean = False
    Private _rotAngle As Single = 0.0F
    Private _fadeAlpha As Single = 0.0F

    Private _interaction As TimelineInteractionState = TimelineInteractionState.Idle
    Private ReadOnly _slipStartPhysTime As TimeSpan
    Private ReadOnly _slipStartMarkerStart As TimeSpan

    Private _isDraggingVolume As Boolean = False
    Private ReadOnly _dragStartY As Integer = 0
    Private ReadOnly _startTrackVolume As Single = 1.0F
    Private _audioTrackRect As System.Drawing.Rectangle

    ' --- ПЕРЕМЕННЫЕ ДЛЯ DRAG & DROP ---
    Private _isDraggingClip As Boolean = False
    Private _draggedClipOriginal As MediaClip = Nothing
    Private _dragCursorOffsetX As Double = 0
    Private _ghostStartTime As TimeSpan
    Private _ghostTrackId As Guid
    Private _isGhostDropValid As Boolean = False
    Private _brushGhostValid As SolidColorBrush
    Private _brushGhostInvalid As SolidColorBrush

    Public Event PlayheadScrubbed As Action(Of TimeSpan) Implements ITimelineRenderer.PlayheadScrubbed
    Public Event PlayheadSeekCompleted As Action(Of TimeSpan) Implements ITimelineRenderer.PlayheadSeekCompleted
    Public Event MarkerStartChanged As Action(Of TimeSpan) Implements ITimelineRenderer.MarkerStartChanged
    Public Event MarkerEndChanged As Action(Of TimeSpan) Implements ITimelineRenderer.MarkerEndChanged
    Public Event MarkersCommit As Action Implements ITimelineRenderer.MarkersCommit
    Public Event AudioOffsetChanged As Action(Of TimeSpan) Implements ITimelineRenderer.AudioOffsetChanged
    Public Event AudioOffsetCommit As Action(Of TimeSpan) Implements ITimelineRenderer.AudioOffsetCommit
    Public Event PreviewRequested As Action(Of TimeSpan) Implements ITimelineRenderer.PreviewRequested
    Public Event PlaybackPauseRequested As Action Implements ITimelineRenderer.PlaybackPauseRequested
    Public Event CursorMoved As Action(Of TimeSpan, Integer) Implements ITimelineRenderer.CursorMoved
    Public Event CursorLeft As Action Implements ITimelineRenderer.CursorLeft
    Public Event DeviceRecreated As Action Implements ITimelineRenderer.DeviceRecreated
    Public Event LogMessage As Action(Of String) Implements ITimelineRenderer.LogMessage

    Public Sub UpdateLayout(newTileSize As Drawing.Size, newTileCount As Integer) Implements ITimelineRenderer.UpdateLayout
        _tileSize = newTileSize
        ThumbCount = newTileCount
    End Sub

    Public Sub New(Optional defaultTileSize As Drawing.Size = Nothing)
        If _state Is Nothing Then _state = New TimelineStateData()
        If _state.CutRegions Is Nothing Then _state.CutRegions = New List(Of CutRegionData)()
        If defaultTileSize.IsEmpty Then
            _tileSize = New Drawing.Size(120, 68)
        Else
            _tileSize = defaultTileSize
        End If
    End Sub

    Public Sub AddAudioPeaksCache(filePath As String, peaks() As IServices.PeakMinMax, samplesPerPeak As Integer)
        SyncLock _renderLock
            _audioPeaksCache(filePath) = peaks
            If samplesPerPeak > 0 Then
                _peaksPerSecond = 48000.0 / samplesPerPeak
            End If
        End SyncLock
        SafeInvalidate()
    End Sub

    Private Sub UpdateScrollbarVisibility()
        UpdateTotalTimelineWidth()
        _isScrollbarVisible = _totalTimelineWidth > _currentWidth
        _contentHeightWithoutScroll = If(_isScrollbarVisible, _currentHeight - ScrollbarHeight, _currentHeight)
    End Sub

    Private Sub UpdateTotalTimelineWidth()
        _totalTimelineWidth = CDbl(_contentWidth) * _zoomLevel
    End Sub

    Private Function GetTimelineTrackHeight() As Single
        Return _contentHeightWithoutScroll - _timelineHeight
    End Function

    Private Function GetAudioTrackRect() As System.Drawing.RectangleF
        Dim trackCount As Integer = Math.Max(1, If(_state.Tracks IsNot Nothing, _state.Tracks.Count, 2))
        Dim trackHeight As Single = GetTimelineTrackHeight() / trackCount

        ' Динамически ищем первую аудио-дорожку
        Dim audioIdx As Integer = trackCount - 1
        If _state.Tracks IsNot Nothing Then
            For i As Integer = 0 To _state.Tracks.Count - 1
                If _state.Tracks(i).Type = TargetFormatType.Audio Then
                    audioIdx = i
                    Exit For
                End If
            Next
        End If

        Dim audioTop As Single = _timelineHeight + (audioIdx * trackHeight)
        Return New System.Drawing.RectangleF(Padding, audioTop, Math.Max(1.0F, _currentWidth - 2 * Padding), trackHeight)
    End Function

    Private Function GetActualTileCount() As Integer
        If _boundControl Is Nothing OrElse _boundControl.IsDisposed Then Return Math.Max(8, ThumbCount)
        Dim contentWidth As Integer = _boundControl.Width - 2 * Padding
        If contentWidth <= 0 Then Return Math.Max(8, ThumbCount)

        Dim aspect As Double = 16.0 / 9.0
        Dim firstCache = _caches.Values.FirstOrDefault()
        If firstCache IsNot Nothing AndAlso firstCache.SlotWidth > 0 AndAlso firstCache.SlotHeight > 0 Then
            aspect = CDbl(firstCache.SlotWidth) / CDbl(firstCache.SlotHeight)
        End If

        Dim trackCount As Integer = Math.Max(1, If(_state.Tracks IsNot Nothing, _state.Tracks.Count, 2))
        Dim trackHeight As Single = GetTimelineTrackHeight() / trackCount
        If trackHeight < 10.0F Then trackHeight = 10.0F

        Dim naturalThumbWidth As Double = trackHeight * aspect
        If naturalThumbWidth < 1.0 Then naturalThumbWidth = 1.0

        Dim naturalTileCount As Integer = CInt(Math.Ceiling(contentWidth / naturalThumbWidth))
        Return Math.Max(8, naturalTileCount)
    End Function

    Public Function GetThumbSize() As Size Implements ITimelineRenderer.GetThumbSize
        If _boundControl Is Nothing OrElse _boundControl.IsDisposed Then Return _tileSize
        Dim contentWidth As Integer = _boundControl.Width - 2 * Padding
        If contentWidth <= 0 Then contentWidth = 1
        Dim trackCount As Integer = Math.Max(1, If(_state.Tracks IsNot Nothing, _state.Tracks.Count, 2))
        Dim trackHeight As Single = GetTimelineTrackHeight() / trackCount
        Dim tileCount As Integer = GetActualTileCount()
        Dim thumbWidth As Integer = CInt(Math.Ceiling(CSng(contentWidth) / tileCount))
        thumbWidth = (thumbWidth \ 2) * 2
        Dim tHeight As Integer = (CInt(Math.Floor(trackHeight)) \ 2) * 2
        If thumbWidth < 40 Then thumbWidth = 40
        If tHeight < 10 Then tHeight = 10
        Return New Size(thumbWidth, tHeight)
    End Function

    Public Sub Initialize(pb As Control) Implements ITimelineRenderer.Initialize
        ArgumentNullException.ThrowIfNull(pb)

        RemoveHandler ThemeManager.ThemeChanged, AddressOf OnThemeChanged
        If _boundControl IsNot Nothing Then
            RemoveHandler _boundControl.Paint, AddressOf Control_Paint
            RemoveHandler _boundControl.MouseDown, AddressOf Control_MouseDown
            RemoveHandler _boundControl.MouseMove, AddressOf Control_MouseMove
            RemoveHandler _boundControl.MouseUp, AddressOf Control_MouseUp
            RemoveHandler _boundControl.MouseLeave, AddressOf Control_MouseLeave
            RemoveHandler _boundControl.MouseWheel, AddressOf Control_MouseWheel

            Dim oldPicBox = TryCast(_boundControl, PictureBox)
            If oldPicBox IsNot Nothing Then
                RemoveHandler oldPicBox.MouseCaptureChanged, AddressOf Control_MouseCaptureChanged
            End If
        End If

        _boundControl = pb

        AddHandler _boundControl.Paint, AddressOf Control_Paint
        AddHandler _boundControl.MouseDown, AddressOf Control_MouseDown
        AddHandler _boundControl.MouseMove, AddressOf Control_MouseMove
        AddHandler _boundControl.MouseUp, AddressOf Control_MouseUp
        AddHandler _boundControl.MouseLeave, AddressOf Control_MouseLeave
        AddHandler _boundControl.MouseWheel, AddressOf Control_MouseWheel

        Dim picBox = TryCast(_boundControl, PictureBox)
        If picBox IsNot Nothing Then
            AddHandler picBox.MouseCaptureChanged, AddressOf Control_MouseCaptureChanged
        End If

        AddHandler ThemeManager.ThemeChanged, AddressOf OnThemeChanged

        _factory = New Direct2D.Factory(Direct2D.FactoryType.MultiThreaded)
        _dwFactory = New DirectWrite.Factory()
        CreateDeviceResources()
    End Sub

    Public Sub RecreateResources()
        DiscardDeviceResources()
        SafeInvalidate()
    End Sub

    Public Sub Resize(width As Integer, height As Integer) Implements ITimelineRenderer.Resize
        SyncLock _renderLock
            _currentWidth = width
            _currentHeight = height
            _timelineHeight = 14.0F
            _contentWidth = width - 2 * Padding
            UpdateScrollbarVisibility()

            Dim audRect = GetAudioTrackRect()
            _audioTrackRect = New System.Drawing.Rectangle(CInt(audRect.Left), CInt(audRect.Top), CInt(audRect.Width), CInt(audRect.Height))

            Try
                If _isDeviceValid AndAlso _swapChain IsNot Nothing AndAlso _renderTarget IsNot Nothing Then
                    PerformFastResize(width, height)
                ElseIf Not _isDeviceValid Then
                    CreateDeviceResources()
                End If
            Catch ex As SharpDX.SharpDXException
                _isDeviceValid = False
                DiscardDeviceResources()
            Catch ex As Exception
                _isDeviceValid = False
                DiscardDeviceResources()
            End Try
        End SyncLock
        SafeInvalidate()
    End Sub
    Private Sub PerformFastResize(width As Integer, height As Integer)
        ClearVramBitmaps()
        For Each kv In _strips
            If kv.Value IsNot Nothing AndAlso Not kv.Value.IsDisposed Then kv.Value.Dispose()
        Next
        _strips.Clear()

        If _previewBitmap IsNot Nothing AndAlso Not _previewBitmap.IsDisposed Then
            _previewBitmap.Dispose()
            _previewBitmap = Nothing
        End If

        DisposeRenderTargetAndBrushes()

        ' ИСПРАВЛЕНИЕ: Удалены блокирующие вызовы GC.Collect() и GC.WaitForPendingFinalizers().
        ' COM-объекты DirectX освобождаются через DisposeRenderTargetAndBrushes(), 
        ' поэтому вызов сборщика мусора здесь был избыточен и замораживал UI.

        _swapChain.ResizeBuffers(2, Math.Max(1, width), Math.Max(1, height), DXGI.Format.B8G8R8A8_UNorm, DXGI.SwapChainFlags.None)
        CreateRenderTargetAndBrushes()
        UpdateBrushColors(ThemeManager.IsDarkTheme)
    End Sub

    Private Sub CreateDeviceResources()
        If _boundControl Is Nothing OrElse _boundControl.IsDisposed OrElse _boundControl.ClientSize.Width <= 0 OrElse _boundControl.ClientSize.Height <= 0 Then Return

        SyncLock _renderLock
            If _isDeviceValid AndAlso _renderTarget IsNot Nothing Then Return
            DiscardDeviceResources()

            Try
                Dim hwnd = _boundControl.Handle
                Dim width = Math.Max(1, _boundControl.ClientSize.Width)
                Dim height = Math.Max(1, _boundControl.ClientSize.Height)

                Dim desc As New DXGI.SwapChainDescription() With {
                    .BufferCount = 2,
                    .ModeDescription = New DXGI.ModeDescription(width, height, New DXGI.Rational(60, 1), DXGI.Format.B8G8R8A8_UNorm),
                    .IsWindowed = True,
                    .OutputHandle = hwnd,
                    .SampleDescription = New DXGI.SampleDescription(1, 0),
                    .SwapEffect = DXGI.SwapEffect.Discard,
                    .Usage = DXGI.Usage.RenderTargetOutput
                }

                D3D11.Device.CreateWithSwapChain(D3D.DriverType.Hardware, D3D11.DeviceCreationFlags.BgraSupport, desc, _d3dDevice, _swapChain)

                Using mt = _d3dDevice.QueryInterface(Of D3D11.Multithread)()
                    mt?.SetMultithreadProtected(True)
                End Using

                CreateGeometries()
                CreateRenderTargetAndBrushes()

                _isDeviceValid = True
                UpdateBrushColors(ThemeManager.IsDarkTheme)

                RaiseEvent DeviceRecreated()
            Catch ex As Exception
                DiscardDeviceResources()
            End Try
        End SyncLock
    End Sub

    Private Sub CreateRenderTargetAndBrushes()
        Using dxgiDevice As DXGI.Device = _d3dDevice.QueryInterface(Of DXGI.Device)()
            Using backBuffer As D3D11.Texture2D = _swapChain.GetBackBuffer(Of D3D11.Texture2D)(0)
                Using dxgiSurface As DXGI.Surface = backBuffer.QueryInterface(Of DXGI.Surface)()
                    Dim rtp As New RenderTargetProperties(New PixelFormat(DXGI.Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied))
                    _renderTarget = New Direct2D.RenderTarget(_factory, dxgiSurface, rtp) With {.AntialiasMode = AntialiasMode.PerPrimitive}
                End Using
            End Using
        End Using

        Dim dummyColor As New RawColor4(0, 0, 0, 1.0F)

        _brushBg = New SolidColorBrush(_renderTarget, dummyColor)
        _brushText = New SolidColorBrush(_renderTarget, dummyColor)
        _brushOutline = New SolidColorBrush(_renderTarget, dummyColor)
        _brushPlaceholder = New SolidColorBrush(_renderTarget, dummyColor)
        _brushTickLong = New SolidColorBrush(_renderTarget, dummyColor)
        _brushTickShort = New SolidColorBrush(_renderTarget, dummyColor)
        _brushCursor = New SolidColorBrush(_renderTarget, dummyColor)
        _brushTooltipBg = New SolidColorBrush(_renderTarget, dummyColor)
        _brushTooltipBorder = New SolidColorBrush(_renderTarget, dummyColor)
        _brushLoadingBg = New SolidColorBrush(_renderTarget, dummyColor)
        _brushAudioBg = New SolidColorBrush(_renderTarget, dummyColor)
        _brushAudioCenterLine = New SolidColorBrush(_renderTarget, dummyColor)
        _brushAudioSeparator = New SolidColorBrush(_renderTarget, dummyColor)
        _brushAudioBorder = New SolidColorBrush(_renderTarget, dummyColor)
        _brushAudioLabelBg = New SolidColorBrush(_renderTarget, dummyColor)
        _brushAudioLabelBorder = New SolidColorBrush(_renderTarget, dummyColor)
        _brushAudioLabelText = New SolidColorBrush(_renderTarget, dummyColor)
        _brushPlayheadBorder = New SolidColorBrush(_renderTarget, dummyColor)
        _brushMarkerBorder = New SolidColorBrush(_renderTarget, dummyColor)

        _brushLine = New SolidColorBrush(_renderTarget, New RawColor4(0 / 255.0F, 191 / 255.0F, 255 / 255.0F, 1.0F))
        _brushSelection = New SolidColorBrush(_renderTarget, New RawColor4(0 / 255.0F, 120 / 255.0F, 215 / 255.0F, 0.4F))
        _brushMarkerStart = New SolidColorBrush(_renderTarget, New RawColor4(200 / 255.0F, 200 / 255.0F, 200 / 255.0F, 1.0F))
        _brushMarkerEnd = New SolidColorBrush(_renderTarget, New RawColor4(200 / 255.0F, 200 / 255.0F, 200 / 255.0F, 1.0F))
        _brushPlaybackLine = New SolidColorBrush(_renderTarget, New RawColor4(255 / 255.0F, 200 / 255.0F, 0 / 255.0F, 1.0F))
        _brushCutRegion = New SolidColorBrush(_renderTarget, New RawColor4(255 / 255.0F, 50 / 255.0F, 50 / 255.0F, 0.4F))
        _brushGlow = New SolidColorBrush(_renderTarget, New RawColor4(76 / 255.0F, 144 / 255.0F, 240 / 255.0F, 0.8F))
        _brushMain = New SolidColorBrush(_renderTarget, New RawColor4(76 / 255.0F, 144 / 255.0F, 240 / 255.0F, 1.0F))
        _brushSplice = New SolidColorBrush(_renderTarget, New RawColor4(0.9F, 0.2F, 0.2F, 0.85F))
        _brushPlayheadShadow = New SolidColorBrush(_renderTarget, New RawColor4(0.0F, 0.0F, 0.0F, 0.4F))
        _brushPlayheadLine = New SolidColorBrush(_renderTarget, New RawColor4(0.0F, 0.58F, 1.0F, 1.0F))
        _brushPlayheadFill = New SolidColorBrush(_renderTarget, New RawColor4(0.0F, 0.58F, 1.0F, 1.0F))
        _brushMarkerShadow = New SolidColorBrush(_renderTarget, New RawColor4(0.0F, 0.0F, 0.0F, 0.3F))
        _brushMarkerLine = New SolidColorBrush(_renderTarget, New RawColor4(0.9F, 0.9F, 0.92F, 1.0F))
        _brushMarkerFill = New SolidColorBrush(_renderTarget, New RawColor4(0.9F, 0.9F, 0.92F, 1.0F))
        _brushMarkerAccent = New SolidColorBrush(_renderTarget, New RawColor4(0.0F, 0.6F, 1.0F, 1.0F))

        _brushDim = New SolidColorBrush(_renderTarget, New RawColor4(0.0F, 0.0F, 0.0F, 0.65F))
        _brushFadeLine = New SolidColorBrush(_renderTarget, New RawColor4(0.9F, 0.9F, 0.9F, 1.0F))
        _brushFadeHandle = New SolidColorBrush(_renderTarget, New RawColor4(0.2F, 0.6F, 1.0F, 0.9F))
        _brushMask = New SolidColorBrush(_renderTarget, New RawColor4(0.0F, 0.0F, 0.0F, 0.65F))

        _brushAudioWaveformL = New SolidColorBrush(_renderTarget, New RawColor4(0.0F, 0.75F, 1.0F, 1.0F))
        _brushAudioWaveformR = New SolidColorBrush(_renderTarget, New RawColor4(0.0F, 1.0F, 0.5F, 1.0F))

        _brushGhostValid = New SolidColorBrush(_renderTarget, New RawColor4(0.2F, 0.8F, 0.2F, 0.4F))
        _brushGhostInvalid = New SolidColorBrush(_renderTarget, New RawColor4(0.9F, 0.2F, 0.2F, 0.4F))

        _audioGradientStops = New GradientStopCollection(_renderTarget, {
            New GradientStop() With {.Position = 0.0F, .Color = New RawColor4(0.2F, 0.8F, 0.2F, 0.3F)},
            New GradientStop() With {.Position = 0.5F, .Color = New RawColor4(1.0F, 1.0F, 0.0F, 0.3F)},
            New GradientStop() With {.Position = 1.0F, .Color = New RawColor4(1.0F, 0.2F, 0.2F, 0.3F)}
        })
    End Sub

    Private Sub CreateGeometries()
        DisposeGeometry(_startMarkerGeom)
        DisposeGeometry(_endMarkerGeom)
        DisposeGeometry(_loadingArcGeom)

        _startMarkerGeom = New PathGeometry(_factory)
        Using sink As GeometrySink = _startMarkerGeom.Open()
            sink.BeginFigure(New RawVector2(0.0F, 0.0F), FigureBegin.Filled)
            sink.AddLines({New RawVector2(11.0F, 0.0F), New RawVector2(11.0F, 9.0F), New RawVector2(5.5F, 14.0F), New RawVector2(0.0F, 14.0F)})
            sink.EndFigure(FigureEnd.Closed)
            sink.Close()
        End Using

        _endMarkerGeom = New PathGeometry(_factory)
        Using sink As GeometrySink = _endMarkerGeom.Open()
            sink.BeginFigure(New RawVector2(0.0F, 0.0F), FigureBegin.Filled)
            sink.AddLines({New RawVector2(-11.0F, 0.0F), New RawVector2(-11.0F, 9.0F), New RawVector2(-5.5F, 14.0F), New RawVector2(0.0F, 14.0F)})
            sink.EndFigure(FigureEnd.Closed)
            sink.Close()
        End Using

        _loadingArcGeom = New PathGeometry(_factory)
        Using sink As GeometrySink = _loadingArcGeom.Open()
            Dim startRad As Double = -90.0 * Math.PI / 180.0
            Dim endRad As Double = (140.0 - 90.0) * Math.PI / 180.0
            sink.BeginFigure(New RawVector2(CSng(28.0F * Math.Cos(startRad)), CSng(28.0F * Math.Sin(startRad))), FigureBegin.Hollow)
            sink.AddArc(New ArcSegment() With {.Point = New RawVector2(CSng(28.0F * Math.Cos(endRad)), CSng(28.0F * Math.Sin(endRad))), .Size = New Size2F(28.0F, 28.0F), .SweepDirection = SweepDirection.Clockwise, .ArcSize = ArcSize.Small})
            sink.EndFigure(FigureEnd.Open)
            sink.Close()
        End Using
    End Sub

    Private Sub DisposeRenderTargetAndBrushes()
        DisposeBrush(_brushBg)
        DisposeBrush(_brushLine)
        DisposeBrush(_brushText)
        DisposeBrush(_brushOutline)
        DisposeBrush(_brushPlaceholder)
        DisposeBrush(_brushTickLong)
        DisposeBrush(_brushTickShort)
        DisposeBrush(_brushCursor)
        DisposeBrush(_brushTooltipBg)
        DisposeBrush(_brushTooltipBorder)
        DisposeBrush(_brushLoadingBg)
        DisposeBrush(_brushAudioBg)
        DisposeBrush(_brushAudioCenterLine)
        DisposeBrush(_brushAudioSeparator)
        DisposeBrush(_brushAudioBorder)
        DisposeBrush(_brushAudioLabelBg)
        DisposeBrush(_brushAudioLabelBorder)
        DisposeBrush(_brushAudioLabelText)
        DisposeBrush(_brushPlayheadBorder)
        DisposeBrush(_brushPlayheadShadow)
        DisposeBrush(_brushPlayheadLine)
        DisposeBrush(_brushPlayheadFill)
        DisposeBrush(_brushMarkerBorder)
        DisposeBrush(_brushMarkerLine)
        DisposeBrush(_brushMarkerFill)
        DisposeBrush(_brushMarkerShadow)
        DisposeBrush(_brushMarkerAccent)
        DisposeBrush(_brushSplice)
        DisposeBrush(_brushCutRegion)
        DisposeBrush(_brushGlow)
        DisposeBrush(_brushMain)
        DisposeBrush(_brushSelection)
        DisposeBrush(_brushMarkerStart)
        DisposeBrush(_brushMarkerEnd)
        DisposeBrush(_brushPlaybackLine)
        DisposeBrush(_brushDim)
        DisposeBrush(_brushFadeLine)
        DisposeBrush(_brushFadeHandle)
        DisposeBrush(_brushMask)

        DisposeBrush(_brushAudioWaveformL)
        DisposeBrush(_brushAudioWaveformR)

        DisposeBrush(_brushGhostValid)
        DisposeBrush(_brushGhostInvalid)

        If _audioGradientStops IsNot Nothing Then
            _audioGradientStops.Dispose()
            _audioGradientStops = Nothing
        End If
        If _audioGradientBrush IsNot Nothing Then
            _audioGradientBrush.Dispose()
            _audioGradientBrush = Nothing
        End If

        _cachedGradientTop = -1.0F
        _cachedGradientBottom = -1.0F

        If _renderTarget IsNot Nothing Then
            _renderTarget.Dispose()
            _renderTarget = Nothing
        End If
    End Sub

    Private Sub DiscardDeviceResources()
        SyncLock _renderLock
            _isDeviceValid = False

            ClearVramBitmaps()
            For Each kv In _strips
                If kv.Value IsNot Nothing AndAlso Not kv.Value.IsDisposed Then kv.Value.Dispose()
            Next
            _strips.Clear()

            If _previewBitmap IsNot Nothing AndAlso Not _previewBitmap.IsDisposed Then
                _previewBitmap.Dispose()
                _previewBitmap = Nothing
            End If

            DisposeGeometry(_startMarkerGeom)
            DisposeGeometry(_endMarkerGeom)
            DisposeGeometry(_loadingArcGeom)

            DisposeRenderTargetAndBrushes()

            If _swapChain IsNot Nothing Then
                _swapChain.Dispose()
                _swapChain = Nothing
            End If
            If _d3dDevice IsNot Nothing Then
                _d3dDevice.Dispose()
                _d3dDevice = Nothing
            End If
        End SyncLock
    End Sub

    Private Shared Sub DisposeBrush(ByRef brush As SolidColorBrush)
        If brush IsNot Nothing Then brush.Dispose() : brush = Nothing
    End Sub

    Private Shared Sub DisposeGeometry(ByRef geom As PathGeometry)
        If geom IsNot Nothing Then geom.Dispose() : geom = Nothing
    End Sub

    Private Sub ClearVramBitmaps()
        For Each kv In _cancellationTokens
            Try
                kv.Value.Cancel()
                kv.Value.Dispose()
            Catch
            End Try
        Next
        _cancellationTokens.Clear()
        _activeFrameTasks.Clear()

        For Each kv In _thumbnails
            If kv.Value IsNot Nothing Then kv.Value.Dispose()
        Next
        _thumbnails.Clear()
    End Sub

    Private Sub OnThemeChanged(sender As Object, e As ThemeChangedEventArgs)
        SyncLock _renderLock
            _isDarkTheme = e.IsDark
            If _isDeviceValid Then UpdateBrushColors(e.IsDark) Else CreateDeviceResources()
        End SyncLock
        SafeInvalidate()
    End Sub

    Private Sub UpdateBrushColors(isDark As Boolean)
        If Not _isDeviceValid OrElse _renderTarget Is Nothing Then Return
        Dim palette = ThemePalette.GetCurrent(isDark)
        _brushBg.Color = palette.Background
        _brushText.Color = palette.Text
        _brushOutline.Color = palette.Outline
        _brushPlaceholder.Color = palette.Placeholder
        _brushTickLong.Color = palette.TickLong
        _brushTickShort.Color = palette.TickShort
        _brushCursor.Color = palette.Cursor
        _brushTooltipBg.Color = palette.TooltipBg
        _brushTooltipBorder.Color = palette.TooltipBorder
        _brushLoadingBg.Color = palette.LoadingBg
        _brushAudioBg.Color = palette.AudioBg
        _brushAudioCenterLine.Color = palette.AudioCenterLine
        _brushAudioSeparator.Color = palette.AudioSeparator
        _brushAudioBorder.Color = palette.AudioBorder
        _brushAudioLabelBg.Color = palette.AudioLabelBg
        _brushAudioLabelBorder.Color = palette.AudioLabelBorder
        _brushAudioLabelText.Color = palette.AudioLabelText
        _brushPlayheadBorder.Color = palette.PlayheadBorder
        _brushMarkerBorder.Color = palette.MarkerBorder
        If _brushMarkerLine IsNot Nothing Then _brushMarkerLine.Color = palette.MarkerBorder
        If _brushMarkerFill IsNot Nothing Then _brushMarkerFill.Color = palette.MarkerBorder
    End Sub

    Private Function GetOrUpdateD2DBitmap(ByRef targetBitmap As Bitmap, rawBytes As Byte(), width As Integer, height As Integer) As Bitmap
        If rawBytes Is Nothing OrElse rawBytes.Length = 0 OrElse width <= 0 OrElse height <= 0 Then Return targetBitmap
        Dim pitch As Integer = width * 4
        Dim expectedBytes As Integer = pitch * height
        If rawBytes.Length < expectedBytes Then Return targetBitmap

        Dim handle As GCHandle = GCHandle.Alloc(rawBytes, GCHandleType.Pinned)
        Try
            Dim dataPtr As IntPtr = handle.AddrOfPinnedObject()
            If targetBitmap IsNot Nothing AndAlso Not targetBitmap.IsDisposed AndAlso
               targetBitmap.PixelSize.Width = width AndAlso targetBitmap.PixelSize.Height = height Then
                targetBitmap.CopyFromMemory(dataPtr, pitch)
                Return targetBitmap
            Else
                If targetBitmap IsNot Nothing Then
                    targetBitmap.Dispose()
                    targetBitmap = Nothing
                End If

                Dim pixelFormat As New PixelFormat(DXGI.Format.B8G8R8A8_UNorm, AlphaMode.Ignore)
                Dim bmpProps As New BitmapProperties(pixelFormat)
                Dim size As New Size2(width, height)
                Dim d2dDataPointer As New DataPointer(dataPtr, expectedBytes)
                Return New Bitmap(_renderTarget, size, d2dDataPointer, pitch, bmpProps)
            End If
        Finally
            If handle.IsAllocated Then handle.Free()
        End Try
    End Function

    Public Sub SetDataSources(caches As Object, extractors As Object) Implements ITimelineRenderer.SetDataSources
        SyncLock _renderLock
            For Each cache In _caches.Values
                RemoveHandler cache.FrameCached, AddressOf OnFrameCached
            Next

            _caches.Clear()
            _extractors.Clear()

            Dim typedCaches = TryCast(caches, Dictionary(Of String, Object))
            Dim typedExtractors = TryCast(extractors, Dictionary(Of String, Object))

            If typedCaches IsNot Nothing Then
                For Each kvp In typedCaches
                    Dim typedCache = TryCast(kvp.Value, GpuFrameCacheManager)
                    If typedCache IsNot Nothing Then
                        _caches.Add(kvp.Key, typedCache)
                        AddHandler typedCache.FrameCached, AddressOf OnFrameCached
                    End If
                Next
            End If

            If typedExtractors IsNot Nothing Then
                For Each kvp In typedExtractors
                    Dim typedExt = TryCast(kvp.Value, GpuFrameExtractor)
                    If typedExt IsNot Nothing Then _extractors.Add(kvp.Key, typedExt)
                Next
            End If

            ClearVramBitmaps()
        End SyncLock
        SafeInvalidate()
    End Sub

    Private Sub OnFrameCached(index As Integer)
        SafeInvalidate()
    End Sub

    Public Sub SetAudioPeaks(peaks() As IServices.PeakMinMax, samplesPerPeak As Integer) Implements ITimelineRenderer.SetAudioPeaks
        SyncLock _renderLock
            _audioPeaks = peaks
            If samplesPerPeak > 0 Then
                _peaksPerSecond = 48000.0 / samplesPerPeak
            End If
        End SyncLock
        SafeInvalidate()
    End Sub

    Public Sub UpdateState(state As TimelineStateData, fps As Double, hasSelection As Boolean, isAudioReplaced As Boolean, hasAudio As Boolean) Implements ITimelineRenderer.UpdateState
        If state Is Nothing Then Return
        SyncLock _renderLock
            _state = state
            If _state.CutRegions IsNot Nothing Then
                _state.CutRegions = state.CutRegions.ToList()
            Else
                _state.CutRegions = New List(Of CutRegionData)()
            End If

            _fps = fps
            _hasSelection = hasSelection
            _isAudioReplaced = isAudioReplaced
            _hasAudio = hasAudio
        End SyncLock
        SafeInvalidate()
    End Sub

    Public Sub UpdatePlayhead(timePosition As TimeSpan) Implements ITimelineRenderer.UpdatePlayhead
        Dim oldX As Integer = _currentPlaybackX
        _playheadTime = timePosition
        _currentPlaybackX = GetPhysicalXFromTime(timePosition)

        If oldX = _currentPlaybackX Then Return

        Dim topY As Integer = 0
        Dim bottomY As Integer = CInt(Math.Ceiling(_currentHeight))
        Dim playheadWidth As Integer = 24

        Dim oldRect As New System.Drawing.Rectangle(oldX - (playheadWidth \ 2), topY, playheadWidth, bottomY - topY)
        Dim newRect As New System.Drawing.Rectangle(_currentPlaybackX - (playheadWidth \ 2), topY, playheadWidth, bottomY - topY)

        SafeInvalidate(oldRect)
        SafeInvalidate(newRect)
    End Sub

    Public Sub UpdateAudioOffset(offset As TimeSpan, bakedOffset As TimeSpan) Implements ITimelineRenderer.UpdateAudioOffset
        If _audioOffset = offset AndAlso _bakedAudioOffset = bakedOffset Then Return
        _audioOffset = offset
        _bakedAudioOffset = bakedOffset
        SafeInvalidate()
    End Sub

    Public Sub UpdateLoadingState(isLoading As Boolean, rotAngle As Single, fadeAlpha As Single) Implements ITimelineRenderer.UpdateLoadingState
        _isLoading = isLoading
        _rotAngle = rotAngle
        _fadeAlpha = fadeAlpha
        If _isLoading Then RenderInternal()
    End Sub

    Public Function LoadStripAsync(targetIndex As Integer, tempFilePath As String) As Task Implements ITimelineRenderer.LoadStripAsync
        Return Task.CompletedTask
    End Function

    Public Sub ClearStrips() Implements ITimelineRenderer.ClearStrips
        SyncLock _renderLock
            _audioPeaksCache.Clear()
            _audioPeaks = Nothing
            For Each bmp In _strips.Values
                If bmp IsNot Nothing Then bmp.Dispose()
            Next
            _strips.Clear()
        End SyncLock
        SafeInvalidate()
    End Sub

    Public Sub UpdatePreviewFromRawBytes(rawBytes() As Byte, width As Integer, height As Integer) Implements ITimelineRenderer.UpdatePreviewFromRawBytes
        SyncLock _renderLock
            If _renderTarget IsNot Nothing AndAlso Not _renderTarget.IsDisposed Then
                Try
                    _previewBitmap = GetOrUpdateD2DBitmap(_previewBitmap, rawBytes, width, height)
                Catch ex As Exception
                End Try
            End If
        End SyncLock
        SafeInvalidate()
    End Sub

    Private Function GetVirtualDuration() As TimeSpan
        SyncLock _renderLock
            If _projectModel IsNot Nothing Then Return _projectModel.VirtualDuration
            Return If(_state IsNot Nothing, _state.Duration, TimeSpan.Zero)
        End SyncLock
    End Function

    Private Function PhysicalToVirtual(physicalTime As TimeSpan) As TimeSpan
        SyncLock _renderLock
            If _projectModel IsNot Nothing Then Return _projectModel.PhysicalToVirtualTime(physicalTime)
            Return physicalTime
        End SyncLock
    End Function

    Private Function VirtualToPhysical(virtualTime As TimeSpan) As TimeSpan
        SyncLock _renderLock
            If _projectModel IsNot Nothing Then Return _projectModel.VirtualToPhysicalTime(virtualTime)
            Return virtualTime
        End SyncLock
    End Function

    Private Function GetTimeFromX(x As Integer) As TimeSpan
        SyncLock _renderLock
            Dim physX As Double = x + _scrollOffset - Padding
            If _totalTimelineWidth <= 0 Then Return TimeSpan.Zero
            Dim relativePos As Double = Math.Max(0.0, Math.Min(1.0, physX / _totalTimelineWidth))
            Dim vDuration As TimeSpan = GetVirtualDuration()
            If vDuration <= TimeSpan.Zero Then Return TimeSpan.Zero
            If _state.IsZoomed Then
                Dim viewVirtStart As TimeSpan = PhysicalToVirtual(_state.ViewStart)
                Dim viewVirtEnd As TimeSpan = PhysicalToVirtual(_state.ViewEnd)
                Dim viewVirtDuration As Double = (viewVirtEnd - viewVirtStart).TotalSeconds
                Return viewVirtStart + TimeSpan.FromSeconds(relativePos * viewVirtDuration)
            Else
                Return TimeSpan.FromSeconds(relativePos * vDuration.TotalSeconds)
            End If
        End SyncLock
    End Function

    Private Function GetPhysicalXFromTime(virtualTime As TimeSpan) As Integer
        SyncLock _renderLock
            If _totalTimelineWidth <= 0 Then Return CInt(Padding)
            Dim vDuration As TimeSpan = GetVirtualDuration()
            If vDuration <= TimeSpan.Zero Then Return CInt(Padding)
            Dim totalSec As Double
            Dim currentSec As Double = virtualTime.TotalSeconds
            If _state.IsZoomed Then
                Dim viewVirtStart As TimeSpan = PhysicalToVirtual(_state.ViewStart)
                Dim viewVirtEnd As TimeSpan = PhysicalToVirtual(_state.ViewEnd)
                totalSec = (viewVirtEnd - viewVirtStart).TotalSeconds
                currentSec -= viewVirtStart.TotalSeconds
            Else
                totalSec = vDuration.TotalSeconds
            End If
            If totalSec <= 0 Then Return CInt(Padding)

            Dim relativePos As Double = currentSec / totalSec
            Return CInt(Math.Round(Padding + relativePos * _totalTimelineWidth - _scrollOffset, MidpointRounding.AwayFromZero))
        End SyncLock
    End Function

    Private Function GetPhysicalTimeFromX(x As Integer) As TimeSpan
        SyncLock _renderLock
            Dim virtTime As TimeSpan = GetTimeFromX(x)
            Return VirtualToPhysical(virtTime)
        End SyncLock
    End Function

    Private ReadOnly Property IsAudioExpected As Boolean
        Get
            SyncLock _renderLock
                Return _hasAudio OrElse _isAudioReplaced
            End SyncLock
        End Get
    End Property

    Private Shared Function FormatTimecode(totalSeconds As Double, fps As Double) As String
        Dim ts As TimeSpan = TimeSpan.FromSeconds(totalSeconds)
        Dim frames As Integer = CInt((totalSeconds - Math.Floor(totalSeconds)) * fps)
        If frames >= fps Then frames = CInt(fps) - 1
        Return $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}:{frames:D2}"
    End Function

    ' =========================================================================================
    ' ВСПОМОГАТЕЛЬНЫЙ МЕТОД ДЛЯ МАГНИТНОГО ПРИЛИПАНИЯ (SNAPPING)
    ' =========================================================================================
    Private Function FindSnapTime(candidateTime As TimeSpan, ignoreClipId As Guid, ByRef isSnapped As Boolean) As TimeSpan
        Dim localSnapped As Boolean = False

        If Control.ModifierKeys = Keys.Alt Then
            isSnapped = False
            Return candidateTime
        End If

        Dim snapThresholdPx As Integer = 10
        Dim candidateX As Integer = GetPhysicalXFromTime(candidateTime)
        Dim bestDistance As Integer = snapThresholdPx + 1
        Dim bestTime As TimeSpan = candidateTime

        Dim checkSnap = Sub(targetTime As TimeSpan)
                            Dim targetX = GetPhysicalXFromTime(targetTime)
                            Dim dist = Math.Abs(targetX - candidateX)
                            If dist <= snapThresholdPx AndAlso dist < bestDistance Then
                                bestDistance = dist
                                bestTime = targetTime
                                localSnapped = True
                            End If
                        End Sub

        checkSnap(TimeSpan.Zero)
        checkSnap(_playheadTime)
        checkSnap(PhysicalToVirtual(_state.MarkerStart))
        checkSnap(PhysicalToVirtual(_state.MarkerEnd))

        If _projectModel IsNot Nothing Then
            For Each track In _projectModel.Tracks
                For Each clip In track.Clips
                    If clip.Id = ignoreClipId Then Continue For
                    checkSnap(clip.TimelineStart)
                    checkSnap(clip.TimelineEnd)
                Next
            Next
        End If

        isSnapped = localSnapped
        Return bestTime
    End Function


    ' =========================================================================================
    ' ОБРАБОТКА МЫШИ: НАЖАТИЕ
    ' =========================================================================================

    Private Sub Control_MouseDown(sender As Object, e As System.Windows.Forms.MouseEventArgs)
        If _state.Duration <= TimeSpan.Zero Then Return
        If _interaction <> TimelineInteractionState.Idle Then Return

        Dim virtHoverTime As TimeSpan = GetTimeFromX(e.X)

        If e.Button = MouseButtons.Left Then

            ' Устанавливаем желтую линию (плейхед) по клику
            _currentPlaybackX = e.X
            RaiseEvent PlaybackPauseRequested()
            RaiseEvent PlayheadScrubbed(virtHoverTime)

            ' 1. ПРОВЕРКА МАРКЕРОВ
            Dim startX = GetPhysicalXFromTime(PhysicalToVirtual(_state.MarkerStart))
            Dim endX = GetPhysicalXFromTime(PhysicalToVirtual(_state.MarkerEnd))

            If Math.Abs(e.X - startX) <= MarkerHitZone Then
                _interaction = TimelineInteractionState.DraggingStartMarker
                _boundControl.Capture = True
                SafeInvalidate()
                Return
            ElseIf Math.Abs(e.X - endX) <= MarkerHitZone Then
                _interaction = TimelineInteractionState.DraggingEndMarker
                _boundControl.Capture = True
                SafeInvalidate()
                Return
            End If

            ' 2. ПРОВЕРКА КЛИПОВ НА ТРЕКАХ
            Dim trackIdx = GetTrackIndexAtY(e.Y)
            ' ИСПРАВЛЕНИЕ: Берем трек из СНИМКА (_state.Tracks), а не из Модели!
            If trackIdx >= 0 AndAlso _state.Tracks IsNot Nothing Then
                Dim track = _state.Tracks(trackIdx)

                For Each clip In track.Clips
                    Dim clipStartX = GetPhysicalXFromTime(clip.TimelineStart)
                    Dim clipEndX = GetPhysicalXFromTime(clip.TimelineEnd)

                    If e.X >= clipStartX - MarkerHitZone AndAlso e.X <= clipEndX + MarkerHitZone Then
                        ' Запоминаем клип из снимка. Любые изменения в нем сразу отобразятся на экране!
                        _selectedClip = clip
                        _selectedTrack = track
                        _dragStartTimelineTime = virtHoverTime
                        _originalTimelineStart = clip.TimelineStart
                        _originalSourceIn = clip.SourceIn
                        _originalSourceOut = clip.SourceOut
                        _clipSnapshotBeforeDrag = clip.Clone()
                        _trackIdxBeforeDrag = trackIdx

                        RaiseEvent SelectionChanged(_selectedClip)

                        ' Проверяем подрезку слева
                        If Math.Abs(e.X - clipStartX) <= MarkerHitZone Then
                            _interaction = TimelineInteractionState.TrimmingClipLeft
                            ' Проверяем подрезку справа
                        ElseIf Math.Abs(e.X - clipEndX) <= MarkerHitZone Then
                            _interaction = TimelineInteractionState.TrimmingClipRight
                        Else
                            ' Захват в центре - перемещение (Drag&Drop)
                            _interaction = TimelineInteractionState.MovingClip
                            _isDraggingClip = True
                            _draggedClipOriginal = clip
                            _ghostTrackId = track.Id
                            _dragCursorOffsetX = (virtHoverTime - clip.TimelineStart).TotalSeconds
                            _ghostStartTime = clip.TimelineStart
                            _isGhostDropValid = True
                        End If

                        _boundControl.Capture = True
                        SafeInvalidate()
                        Return
                    End If
                Next
            End If

            ' 3. ЕСЛИ КЛИКНУЛИ ПО ПУСТОМУ МЕСТУ (Скраббинг)
            _selectedClip = Nothing
            _selectedTrack = Nothing
            RaiseEvent SelectionChanged(Nothing)

            _interaction = TimelineInteractionState.ScrubbingPlayhead
            _boundControl.Capture = True
            SafeInvalidate()
        End If
    End Sub

    ' =========================================================================================
    ' ОБРАБОТКА МЫШИ: ДВИЖЕНИЕ (Изменение размеров, перемещение)
    ' =========================================================================================

    ' =========================================================================================
    ' ОБРАБОТКА МЫШИ: ДВИЖЕНИЕ (Изменение размеров, перемещение)
    ' =========================================================================================

    Private Sub Control_MouseMove(sender As Object, e As System.Windows.Forms.MouseEventArgs)
        If _state.Duration <= TimeSpan.Zero Then Return

        Dim needsRedraw As Boolean = False
        _currentMouseX = e.X
        _currentMouseY = e.Y

        Dim virtHover As TimeSpan = GetTimeFromX(e.X)
        _hoverTimeStr = FormatTimecode(virtHover.TotalSeconds, _fps)

        RaiseEvent CursorMoved(virtHover, e.X)
        UpdateCursorStyle(e.X, e.Y)

        Select Case _interaction

            Case TimelineInteractionState.MovingClip
                If _isDraggingClip AndAlso _draggedClipOriginal IsNot Nothing AndAlso _state.Tracks IsNot Nothing Then
                    Dim newStartSeconds = virtHover.TotalSeconds - _dragCursorOffsetX
                    If newStartSeconds < 0 Then newStartSeconds = 0

                    Dim rawStart = TimeSpan.FromSeconds(newStartSeconds)
                    Dim isSnapped As Boolean = False
                    _ghostStartTime = FindSnapTime(rawStart, _draggedClipOriginal.Id, isSnapped)
                    _activeSnapTime = If(isSnapped, _ghostStartTime, Nothing)

                    Dim targetTrackIdx As Integer = _trackIdxBeforeDrag

                    Dim hoveredTrackIdx = GetTrackIndexAtY(e.Y)
                    If hoveredTrackIdx >= 0 AndAlso hoveredTrackIdx < _state.Tracks.Count Then
                        Dim hoveredTrack = _state.Tracks(hoveredTrackIdx)
                        If hoveredTrack.Type = _draggedClipOriginal.MediaType Then
                            targetTrackIdx = hoveredTrackIdx
                        End If
                    End If

                    If targetTrackIdx >= 0 AndAlso targetTrackIdx < _state.Tracks.Count Then
                        Dim destTrack = _state.Tracks(targetTrackIdx)
                        _ghostTrackId = destTrack.Id
                        _isGhostDropValid = True

                        Dim clipDur = _draggedClipOriginal.TimelineEnd - _draggedClipOriginal.TimelineStart
                        Dim ghostEndTime = _ghostStartTime + clipDur

                        For Each c In destTrack.Clips
                            If c.Id = _draggedClipOriginal.Id Then Continue For
                            If _ghostStartTime < c.TimelineEnd AndAlso ghostEndTime > c.TimelineStart Then
                                _isGhostDropValid = False
                                Exit For
                            End If
                        Next
                    Else
                        _isGhostDropValid = False
                    End If

                    ' ИСПРАВЛЕНИЕ: Плейхед следует за курсором при перемещении клипа
                    _currentPlaybackX = e.X
                    RaiseEvent PreviewRequested(virtHover)
                    RaiseEvent PlayheadScrubbed(virtHover)

                    needsRedraw = True
                End If

            Case TimelineInteractionState.TrimmingClipLeft
                If _selectedClip IsNot Nothing Then
                    Dim delta As TimeSpan = virtHover - _dragStartTimelineTime
                    Dim rawStart As TimeSpan = _originalTimelineStart + delta

                    Dim isSnapped As Boolean = False
                    Dim snappedStart As TimeSpan = FindSnapTime(rawStart, _selectedClip.Id, isSnapped)
                    _activeSnapTime = If(isSnapped, snappedStart, Nothing)

                    Dim newStart As TimeSpan = If(isSnapped, snappedStart, rawStart)
                    Dim newSourceIn As TimeSpan = _originalSourceIn + (newStart - _originalTimelineStart)

                    If newSourceIn < TimeSpan.Zero Then
                        newStart += (TimeSpan.Zero - newSourceIn)
                        newSourceIn = TimeSpan.Zero
                    End If

                    Dim maxStart = _originalTimelineStart + (_originalSourceOut - _originalSourceIn) - TimeSpan.FromSeconds(0.1)
                    If newStart >= maxStart Then
                        newStart = maxStart
                        newSourceIn = _originalSourceOut - TimeSpan.FromSeconds(0.1)
                    End If

                    If _selectedTrack IsNot Nothing Then
                        For Each c In _selectedTrack.Clips
                            If c.Id = _selectedClip.Id Then Continue For
                            If newStart < c.TimelineEnd AndAlso _selectedClip.TimelineEnd > c.TimelineStart Then
                                newStart = c.TimelineEnd
                                newSourceIn = _originalSourceIn + (newStart - _originalTimelineStart)
                            End If
                        Next
                    End If

                    _selectedClip.TimelineStart = newStart
                    _selectedClip.SourceIn = newSourceIn

                    ' ИСПРАВЛЕНИЕ: Плейхед "прилипает" к краю, который мы тянем, чтобы видеть точный кадр обрезки
                    _currentPlaybackX = GetPhysicalXFromTime(newStart)
                    RaiseEvent PreviewRequested(newStart)
                    RaiseEvent PlayheadScrubbed(newStart)

                    needsRedraw = True
                End If

            Case TimelineInteractionState.TrimmingClipRight
                If _selectedClip IsNot Nothing Then
                    Dim delta As TimeSpan = virtHover - _dragStartTimelineTime
                    Dim originalDuration = _originalSourceOut - _originalSourceIn
                    Dim rawEnd As TimeSpan = _originalTimelineStart + originalDuration + delta

                    Dim isSnapped As Boolean = False
                    Dim snappedEnd As TimeSpan = FindSnapTime(rawEnd, _selectedClip.Id, isSnapped)
                    _activeSnapTime = If(isSnapped, snappedEnd, Nothing)

                    Dim newEnd = If(isSnapped, snappedEnd, rawEnd)
                    Dim newSourceOut As TimeSpan = _originalSourceOut + (newEnd - (_originalTimelineStart + originalDuration))

                    If _selectedClip.SourceDuration > TimeSpan.Zero AndAlso newSourceOut > _selectedClip.SourceDuration Then
                        newSourceOut = _selectedClip.SourceDuration
                        newEnd = _originalTimelineStart + (newSourceOut - _originalSourceIn)
                    End If

                    If newSourceOut <= _selectedClip.SourceIn + TimeSpan.FromSeconds(0.1) Then
                        newSourceOut = _selectedClip.SourceIn + TimeSpan.FromSeconds(0.1)
                        newEnd = _originalTimelineStart + (newSourceOut - _originalSourceIn)
                    End If

                    If _selectedTrack IsNot Nothing Then
                        For Each c In _selectedTrack.Clips
                            If c.Id = _selectedClip.Id Then Continue For
                            If _selectedClip.TimelineStart < c.TimelineEnd AndAlso newEnd > c.TimelineStart Then
                                newEnd = c.TimelineStart
                                newSourceOut = _originalSourceOut + (newEnd - (_originalTimelineStart + originalDuration))
                            End If
                        Next
                    End If

                    _selectedClip.SourceOut = newSourceOut

                    ' ИСПРАВЛЕНИЕ: Плейхед "прилипает" к правому краю
                    _currentPlaybackX = GetPhysicalXFromTime(newEnd)
                    RaiseEvent PreviewRequested(newEnd)
                    RaiseEvent PlayheadScrubbed(newEnd)

                    needsRedraw = True
                End If

            Case TimelineInteractionState.ScrubbingPlayhead
                _currentPlaybackX = e.X
                RaiseEvent PreviewRequested(virtHover)
                RaiseEvent PlayheadScrubbed(virtHover)
                needsRedraw = True

            Case TimelineInteractionState.DraggingStartMarker
                Dim isSnapped As Boolean = False
                Dim snappedVirt = FindSnapTime(virtHover, Guid.Empty, isSnapped)
                _activeSnapTime = If(isSnapped, snappedVirt, Nothing)
                Dim newPhysTime = VirtualToPhysical(If(isSnapped, snappedVirt, virtHover))

                SyncLock _renderLock
                    If newPhysTime >= _state.MarkerEnd Then newPhysTime = _state.MarkerEnd.Subtract(TimeSpan.FromMilliseconds(10))
                    If newPhysTime < TimeSpan.Zero Then newPhysTime = TimeSpan.Zero
                    _state.MarkerStart = newPhysTime
                End SyncLock
                RaiseEvent MarkerStartChanged(newPhysTime)

                ' ИСПРАВЛЕНИЕ: Плейхед следует за маркером (включая магнитное прилипание)
                Dim finalVirt = PhysicalToVirtual(newPhysTime)
                _currentPlaybackX = GetPhysicalXFromTime(finalVirt)
                RaiseEvent PreviewRequested(finalVirt)
                RaiseEvent PlayheadScrubbed(finalVirt)

                needsRedraw = True

            Case TimelineInteractionState.DraggingEndMarker
                Dim isSnapped As Boolean = False
                Dim snappedVirt = FindSnapTime(virtHover, Guid.Empty, isSnapped)
                _activeSnapTime = If(isSnapped, snappedVirt, Nothing)
                Dim newPhysTime = VirtualToPhysical(If(isSnapped, snappedVirt, virtHover))

                SyncLock _renderLock
                    If newPhysTime <= _state.MarkerStart Then newPhysTime = _state.MarkerStart.Add(TimeSpan.FromMilliseconds(10))
                    If newPhysTime > _state.Duration Then newPhysTime = _state.Duration
                    _state.MarkerEnd = newPhysTime
                End SyncLock
                RaiseEvent MarkerEndChanged(newPhysTime)

                ' ИСПРАВЛЕНИЕ: Плейхед следует за маркером
                Dim finalVirt = PhysicalToVirtual(newPhysTime)
                _currentPlaybackX = GetPhysicalXFromTime(finalVirt)
                RaiseEvent PreviewRequested(finalVirt)
                RaiseEvent PlayheadScrubbed(finalVirt)

                needsRedraw = True

        End Select

        If needsRedraw Then SafeInvalidate()
    End Sub

    ' =========================================================================================
    ' ОБРАБОТКА МЫШИ: ОТПУСКАНИЕ (Фиксация результата)
    ' =========================================================================================

    Private Sub Control_MouseUp(sender As Object, e As System.Windows.Forms.MouseEventArgs)
        If _boundControl.Capture Then _boundControl.Capture = False

        Dim action As TimelineInteractionState = _interaction
        _interaction = TimelineInteractionState.Idle
        _activeSnapTime = Nothing

        Dim currentPlayheadTime As TimeSpan = GetTimeFromX(_currentPlaybackX)

        Select Case action

            Case TimelineInteractionState.MovingClip
                If _isDraggingClip AndAlso _draggedClipOriginal IsNot Nothing AndAlso _projectModel IsNot Nothing Then
                    If _isGhostDropValid Then
                        ' Сообщаем реальной модели, что клип переехал
                        Dim moveSuccess = _projectModel.MoveClip(_draggedClipOriginal.Id, _ghostTrackId, _ghostStartTime)

                        ' Если перенос был успешен (не было коллизий внутри ProjectModel)
                        If moveSuccess Then
                            Dim targetTrack = _projectModel.Tracks.FirstOrDefault(Function(t) t.Id = _ghostTrackId)
                            Dim targetTrackIdx = _projectModel.Tracks.ToList().IndexOf(targetTrack)
                            If _clipSnapshotBeforeDrag IsNot Nothing AndAlso targetTrackIdx >= 0 Then
                                Dim updatedClip = targetTrack.Clips.FirstOrDefault(Function(c) c.Id = _draggedClipOriginal.Id)
                                If updatedClip IsNot Nothing Then
                                    ' Записываем действие для Ctrl+Z
                                    _projectModel.UpdateClipStateWithHistory(updatedClip.Id, updatedClip.Clone(), targetTrackIdx, _clipSnapshotBeforeDrag, _trackIdxBeforeDrag)
                                End If
                            End If
                        End If
                    End If
                    _isDraggingClip = False
                    _draggedClipOriginal = Nothing
                    _isGhostDropValid = False
                End If
                RaiseEvent MarkersCommit()
                RaiseEvent PlayheadSeekCompleted(currentPlayheadTime)

            Case TimelineInteractionState.TrimmingClipLeft, TimelineInteractionState.TrimmingClipRight
                If _clipSnapshotBeforeDrag IsNot Nothing AndAlso _projectModel IsNot Nothing AndAlso _selectedTrack IsNot Nothing Then
                    ' Передаем измененный клип (_selectedClip) в реальную модель
                    _projectModel.UpdateClipStateWithHistory(_selectedClip.Id, _selectedClip.Clone(), _trackIdxBeforeDrag, _clipSnapshotBeforeDrag, _trackIdxBeforeDrag)
                End If

                RaiseEvent MarkersCommit()
                RaiseEvent PlayheadSeekCompleted(currentPlayheadTime)

            Case TimelineInteractionState.DraggingStartMarker, TimelineInteractionState.DraggingEndMarker
                RaiseEvent MarkersCommit()
                RaiseEvent PlayheadSeekCompleted(currentPlayheadTime)

            Case TimelineInteractionState.ScrubbingPlayhead
                RaiseEvent PlayheadSeekCompleted(currentPlayheadTime)

        End Select

        SafeInvalidate()
    End Sub



    Private Sub Control_MouseLeave(sender As Object, e As EventArgs)
        _currentMouseX = -1
        _currentMouseY = -1
        _hoverTimeStr = String.Empty
        _activeSnapTime = Nothing
        RaiseEvent CursorLeft()
        SafeInvalidate()
    End Sub

    Private Sub Control_MouseCaptureChanged(sender As Object, e As EventArgs)
        If Not _boundControl.Capture Then
            If _interaction = TimelineInteractionState.AudioShifting Then
                _interaction = TimelineInteractionState.Idle
                RaiseEvent AudioOffsetCommit(_audioOffset)
            End If
            _isDraggingVolume = False
            _isDraggingScrollbar = False
            _activeSnapTime = Nothing
        End If
    End Sub

    Private Sub Control_MouseWheel(sender As Object, e As System.Windows.Forms.MouseEventArgs)
        If _state Is Nothing OrElse _state.Duration <= TimeSpan.Zero Then Return

        If Control.ModifierKeys = Keys.Shift Then
            _scrollOffset -= e.Delta * 0.5
            Dim maxScrollH As Double = _totalTimelineWidth - _currentWidth
            _scrollOffset = Math.Max(0.0, Math.Min(If(maxScrollH > 0, maxScrollH, 0.0), _scrollOffset))
            SafeInvalidate()
            Return
        End If

        Dim clientWidth As Double = _currentWidth
        If clientWidth <= 0 OrElse Double.IsNaN(clientWidth) Then Return

        Dim timeUnderCursor As TimeSpan = GetTimeFromX(e.X)

        Dim zoomFactor As Double = If(e.Delta > 0, 1.15, 0.85)
        Dim newZoom As Double = _zoomLevel * zoomFactor
        newZoom = Math.Max(0.1, Math.Min(newZoom, 100.0))

        If Double.IsNaN(newZoom) OrElse Double.IsInfinity(newZoom) Then Return

        _zoomLevel = newZoom
        UpdateTotalTimelineWidth()

        Dim fullPosAfterZoom As Double
        SyncLock _renderLock
            Dim vDuration As TimeSpan = GetVirtualDuration()
            If vDuration <= TimeSpan.Zero OrElse _totalTimelineWidth <= 0 Then
                fullPosAfterZoom = Padding
            Else
                Dim totalSec As Double
                Dim currentSec As Double = timeUnderCursor.TotalSeconds
                If _state.IsZoomed Then
                    Dim viewVirtStart As TimeSpan = PhysicalToVirtual(_state.ViewStart)
                    Dim viewVirtEnd As TimeSpan = PhysicalToVirtual(_state.ViewEnd)
                    totalSec = (viewVirtEnd - viewVirtStart).TotalSeconds
                    currentSec -= viewVirtStart.TotalSeconds
                Else
                    totalSec = vDuration.TotalSeconds
                End If

                If totalSec <= 0 Then
                    fullPosAfterZoom = Padding
                Else
                    Dim relativePos As Double = currentSec / totalSec
                    fullPosAfterZoom = Padding + relativePos * _totalTimelineWidth
                End If
            End If
        End SyncLock

        Dim targetScrollOffset As Double = fullPosAfterZoom - e.X
        If Double.IsNaN(targetScrollOffset) OrElse Double.IsInfinity(targetScrollOffset) Then Return

        _scrollOffset = targetScrollOffset

        Dim maxScr As Double = _totalTimelineWidth - _currentWidth
        _scrollOffset = Math.Max(0.0, Math.Min(If(maxScr > 0, maxScr, 0.0), _scrollOffset))

        UpdateScrollbarVisibility()
        SafeInvalidate()
    End Sub

    ' Определяет индекс трека по координате Y курсора мыши
    ' =========================================================================================
    ' ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ ПОЗИЦИОНИРОВАНИЯ И КУРСОРОВ
    ' =========================================================================================

    Private Function GetTrackIndexAtY(y As Single) As Integer
        If _state.Tracks Is Nothing OrElse _state.Tracks.Count = 0 Then Return -1
        If y < _timelineHeight Then Return -1 ' Клик по верхней линейке времени

        Dim trackHeight As Single = GetTimelineTrackHeight() / _state.Tracks.Count
        Dim idx As Integer = CInt(Math.Floor((y - _timelineHeight) / trackHeight))

        If idx >= 0 AndAlso idx < _state.Tracks.Count Then
            Return idx
        End If
        Return -1
    End Function

    Private Sub UpdateCursorStyle(mouseX As Integer, mouseY As Integer)
        If _interaction <> TimelineInteractionState.Idle Then Return ' Во время перетаскивания курсор не меняется
        If _boundControl Is Nothing OrElse _state Is Nothing Then Return

        ' 1. МАРКЕРЫ ИМЕЮТ НАИВЫСШИЙ ПРИОРИТЕТ (цепляются по всей высоте таймлайна)
        Dim startX = GetPhysicalXFromTime(PhysicalToVirtual(_state.MarkerStart))
        Dim endX = GetPhysicalXFromTime(PhysicalToVirtual(_state.MarkerEnd))
        If Math.Abs(mouseX - startX) <= MarkerHitZone OrElse Math.Abs(mouseX - endX) <= MarkerHitZone Then
            _boundControl.Cursor = System.Windows.Forms.Cursors.SizeWE
            Return
        End If

        ' 2. ЗОНА ВЕРХНЕЙ ЛИНЕЙКИ (Здесь можно хватать желтый указатель)
        If mouseY <= _timelineHeight Then
            Dim playheadX = _currentPlaybackX
            If Math.Abs(mouseX - playheadX) <= 15 Then
                _boundControl.Cursor = System.Windows.Forms.Cursors.Hand
            Else
                _boundControl.Cursor = System.Windows.Forms.Cursors.Default
            End If
            Return
        End If

        ' 3. ЗОНА КЛИПОВ НА ТРЕКАХ
        Dim trackIdx = GetTrackIndexAtY(mouseY)
        If trackIdx >= 0 AndAlso _state.Tracks IsNot Nothing Then
            Dim track = _state.Tracks(trackIdx)
            For Each clip In track.Clips
                Dim clipStartX = GetPhysicalXFromTime(clip.TimelineStart)
                Dim clipEndX = GetPhysicalXFromTime(clip.TimelineEnd)

                If mouseX >= clipStartX - MarkerHitZone AndAlso mouseX <= clipEndX + MarkerHitZone Then
                    ' Наведение на края клипа (Тримминг)
                    If Math.Abs(mouseX - clipStartX) <= MarkerHitZone OrElse Math.Abs(mouseX - clipEndX) <= MarkerHitZone Then
                        _boundControl.Cursor = System.Windows.Forms.Cursors.SizeWE
                        Return
                    End If

                    ' Наведение в центр клипа (Перемещение)
                    _boundControl.Cursor = System.Windows.Forms.Cursors.SizeAll
                    Return
                End If
            Next
        End If

        ' Свободная зона (Ничего нет)
        _boundControl.Cursor = System.Windows.Forms.Cursors.Default
    End Sub

    Public Sub SafeInvalidate(Optional rect As System.Drawing.Rectangle? = Nothing)
        If _disposed Then Return
        If _boundControl IsNot Nothing AndAlso Not _boundControl.IsDisposed Then
            If _boundControl.InvokeRequired Then
                Try
                    _boundControl.BeginInvoke(New Action(Sub()
                                                             If Not _disposed AndAlso Not _boundControl.IsDisposed Then
                                                                 If rect.HasValue AndAlso Not rect.Value.IsEmpty Then
                                                                     _boundControl.Invalidate(rect.Value)
                                                                 Else
                                                                     _boundControl.Invalidate()
                                                                 End If
                                                             End If
                                                         End Sub))
                Catch ex As Exception
                    RaiseEvent LogMessage("Ошибка перерисовки (SafeInvalidate): " & ex.Message)
                End Try
            Else
                If rect.HasValue AndAlso Not rect.Value.IsEmpty Then
                    _boundControl.Invalidate(rect.Value)
                Else
                    _boundControl.Invalidate()
                End If
            End If
        End If
    End Sub

    Private Sub Control_Paint(sender As Object, e As PaintEventArgs)
        RenderInternal()
    End Sub

    Private Sub RenderInternal()
        If _disposed Then Return
        If Not _isDeviceValid Then
            CreateDeviceResources()
            If Not _isDeviceValid Then Return
        End If

        Dim lockAcquired As Boolean = False
        Try
            Monitor.TryEnter(_renderLock, 30, lockAcquired)
            If Not lockAcquired Then Return

            Try
                _renderTarget.BeginDraw()
                Try
                    _renderTarget.Clear(_brushBg.Color)

                    If _isLoading Then
                        DrawLoadingAnimation()
                        Return
                    End If

                    DrawTrackBackgrounds()
                    DrawTimelineFrames()
                    DrawTrackEnvelopes()
                    DrawOverlays()
                    DrawVolumeOverlay()
                    DrawScrollbar()
                Finally
                    _renderTarget.EndDraw()
                    _swapChain.Present(1, DXGI.PresentFlags.None)
                End Try
            Catch ex As SharpDX.SharpDXException
                If ex.ResultCode.Code = ResultCode.RecreateTarget.Code OrElse
                   ex.ResultCode.Code = SharpDX.DXGI.ResultCode.DeviceRemoved.Result.Code OrElse
                   ex.ResultCode.Code = SharpDX.DXGI.ResultCode.DeviceReset.Result.Code Then
                    _isDeviceValid = False
                    DiscardDeviceResources()
                    SafeInvalidate()
                End If
            Catch ex As Exception
                _isDeviceValid = False
                DiscardDeviceResources()
            End Try
        Finally
            If lockAcquired Then Monitor.Exit(_renderLock)
        End Try
    End Sub

    Private Sub DrawTrackBackgrounds()
        Dim trackCount As Integer = Math.Max(1, If(_state.Tracks IsNot Nothing, _state.Tracks.Count, 2))
        Dim trackHeight As Single = GetTimelineTrackHeight() / trackCount

        For trackIdx As Integer = 0 To trackCount - 1
            Dim trackTop As Single = _timelineHeight + (trackIdx * trackHeight)
            Dim trackBottom As Single = trackTop + trackHeight

            ' Разделитель дорожки
            _renderTarget.DrawLine(New RawVector2(Padding, trackBottom), New RawVector2(_currentWidth - Padding, trackBottom), _brushOutline, 1.0F)

            ' Фон аудио-дорожки (динамический поиск)
            If _state.Tracks IsNot Nothing AndAlso trackIdx < _state.Tracks.Count AndAlso _state.Tracks(trackIdx).Type = TargetFormatType.Audio AndAlso IsAudioExpected Then
                Dim clipRect As New RawRectangleF(Padding, trackTop, _currentWidth - Padding, trackBottom)
                _renderTarget.FillRectangle(clipRect, _brushAudioBg)

                Dim centerY As Single = trackTop + (trackHeight / 2.0F)
                _renderTarget.DrawLine(New RawVector2(Padding, centerY), New RawVector2(_currentWidth - Padding, centerY), _brushAudioCenterLine, 1.0F)

                If _audioGradientStops IsNot Nothing Then
                    If _audioGradientBrush Is Nothing Then
                        _audioGradientBrush = New LinearGradientBrush(_renderTarget, New LinearGradientBrushProperties() With {
                            .StartPoint = New RawVector2(0, trackTop),
                            .EndPoint = New RawVector2(0, trackBottom)
                        }, _audioGradientStops)
                        _cachedGradientTop = trackTop
                        _cachedGradientBottom = trackBottom
                    ElseIf _cachedGradientTop <> trackTop OrElse _cachedGradientBottom <> trackBottom Then
                        _audioGradientBrush.StartPoint = New RawVector2(0, trackTop)
                        _audioGradientBrush.EndPoint = New RawVector2(0, trackBottom)
                        _cachedGradientTop = trackTop
                        _cachedGradientBottom = trackBottom
                    End If
                    _renderTarget.FillRectangle(clipRect, _audioGradientBrush)
                End If
            End If
        Next
    End Sub

    Private Sub DrawAudioWaveform(clip As MediaClip, clipRect As RawRectangleF, clipStartX As Single, clipEndX As Single)
        Dim peaks As IServices.PeakMinMax() = Nothing

        If Not _audioPeaksCache.TryGetValue(clip.FilePath, peaks) Then
            peaks = _audioPeaks
        End If

        If peaks Is Nothing OrElse peaks.Length = 0 Then Return

        Dim clipHeight As Single = clipRect.Bottom - clipRect.Top
        Dim cacheCenterY_L As Single = clipRect.Top + (clipHeight * 0.25F)
        Dim cacheCenterY_R As Single = clipRect.Top + (clipHeight * 0.75F)
        Dim amplitude As Single = (clipHeight * 0.25F) * 0.9F

        Dim srcStartSec As Double = clip.SourceIn.TotalSeconds
        Dim srcEndSec As Double = clip.SourceOut.TotalSeconds

        Dim startIndex As Integer = CInt(Math.Floor(srcStartSec * _peaksPerSecond))
        Dim endIndex As Integer = CInt(Math.Ceiling(srcEndSec * _peaksPerSecond))

        If startIndex < 0 Then startIndex = 0
        If endIndex >= peaks.Length Then endIndex = peaks.Length - 1

        If startIndex < endIndex Then
            _renderTarget.PushAxisAlignedClip(clipRect, AntialiasMode.Aliased)
            Try
                Dim destX As Single = clipStartX
                Dim destRight As Single = clipEndX
                Dim pixelsAvailable As Double = destRight - destX
                Dim pointsCount As Integer = endIndex - startIndex + 1

                Dim stepSize As Integer = 1
                If pixelsAvailable > 0 AndAlso pointsCount > pixelsAvailable * 2 Then
                    stepSize = CInt(Math.Floor(pointsCount / (pixelsAvailable * 2.0)))
                End If
                If stepSize < 1 Then stepSize = 1
                Dim internalStep As Integer = If(stepSize > 200, stepSize \ 200, 1)

                Using geomL As New PathGeometry(_factory)
                    Using sinkL As GeometrySink = geomL.Open()
                        sinkL.BeginFigure(New RawVector2(destX, cacheCenterY_L), FigureBegin.Filled)
                        For i As Integer = startIndex To endIndex Step stepSize
                            Dim maxL As SByte = -128
                            Dim sumL As Long = 0
                            Dim count As Integer = 0
                            For j As Integer = 0 To stepSize - 1 Step internalStep
                                If i + j <= endIndex Then
                                    Dim val As SByte = peaks(i + j).MaxL
                                    If val > maxL Then maxL = val
                                    sumL += val
                                    count += 1
                                End If
                            Next
                            Dim finalVal As Single = If(stepSize > 10 AndAlso count > 0, (maxL * 0.35F) + (CSng(sumL / count) * 0.65F), maxL)
                            Dim timeSec As Double = (i / _peaksPerSecond) - srcStartSec
                            Dim vx As Single = destX + CSng(timeSec / (srcEndSec - srcStartSec) * pixelsAvailable)
                            sinkL.AddLine(New RawVector2(vx, cacheCenterY_L - (finalVal / 127.0F) * amplitude))
                        Next
                        For i As Integer = endIndex - ((endIndex - startIndex) Mod stepSize) To startIndex Step -stepSize
                            Dim minL As SByte = 127
                            Dim sumL As Long = 0
                            Dim count As Integer = 0
                            For j As Integer = 0 To stepSize - 1 Step internalStep
                                If i + j <= endIndex Then
                                    Dim val As SByte = peaks(i + j).MinL
                                    If val < minL Then minL = val
                                    sumL += val
                                    count += 1
                                End If
                            Next
                            Dim finalVal As Single = If(stepSize > 10 AndAlso count > 0, (minL * 0.35F) + (CSng(sumL / count) * 0.65F), minL)
                            Dim timeSec As Double = (i / _peaksPerSecond) - srcStartSec
                            Dim vx As Single = destX + CSng(timeSec / (srcEndSec - srcStartSec) * pixelsAvailable)
                            sinkL.AddLine(New RawVector2(vx, cacheCenterY_L - (finalVal / 127.0F) * amplitude))
                        Next
                        sinkL.EndFigure(FigureEnd.Closed)
                        sinkL.Close()
                    End Using
                    _renderTarget.FillGeometry(geomL, _brushAudioWaveformL)
                End Using

                Using geomR As New PathGeometry(_factory)
                    Using sinkR As GeometrySink = geomR.Open()
                        sinkR.BeginFigure(New RawVector2(destX, cacheCenterY_R), FigureBegin.Filled)
                        For i As Integer = startIndex To endIndex Step stepSize
                            Dim maxR As SByte = -128
                            Dim sumR As Long = 0
                            Dim count As Integer = 0
                            For j As Integer = 0 To stepSize - 1 Step internalStep
                                If i + j <= endIndex Then
                                    Dim val As SByte = peaks(i + j).MaxR
                                    If val > maxR Then maxR = val
                                    sumR += val
                                    count += 1
                                End If
                            Next
                            Dim finalVal As Single = If(stepSize > 10 AndAlso count > 0, (maxR * 0.35F) + (CSng(sumR / count) * 0.65F), maxR)
                            Dim timeSec As Double = (i / _peaksPerSecond) - srcStartSec
                            Dim vx As Single = destX + CSng(timeSec / (srcEndSec - srcStartSec) * pixelsAvailable)
                            sinkR.AddLine(New RawVector2(vx, cacheCenterY_R - (finalVal / 127.0F) * amplitude))
                        Next
                        For i As Integer = endIndex - ((endIndex - startIndex) Mod stepSize) To startIndex Step -stepSize
                            Dim minR As SByte = 127
                            Dim sumR As Long = 0
                            Dim count As Integer = 0
                            For j As Integer = 0 To stepSize - 1 Step internalStep
                                If i + j <= endIndex Then
                                    Dim val As SByte = peaks(i + j).MinR
                                    If val < minR Then minR = val
                                    sumR += val
                                    count += 1
                                End If
                            Next
                            Dim finalVal As Single = If(stepSize > 10 AndAlso count > 0, (minR * 0.35F) + (CSng(sumR / count) * 0.65F), minR)
                            Dim timeSec As Double = (i / _peaksPerSecond) - srcStartSec
                            Dim vx As Single = destX + CSng(timeSec / (srcEndSec - srcStartSec) * pixelsAvailable)
                            sinkR.AddLine(New RawVector2(vx, cacheCenterY_R - (finalVal / 127.0F) * amplitude))
                        Next
                        sinkR.EndFigure(FigureEnd.Closed)
                        sinkR.Close()
                    End Using
                    _renderTarget.FillGeometry(geomR, _brushAudioWaveformR)
                End Using
            Finally
                _renderTarget.PopAxisAlignedClip()
            End Try
        End If
    End Sub

    Private Sub DrawTimelineFrames()
        If _caches.Count = 0 OrElse _state.Duration <= TimeSpan.Zero Then
            Using tf As New DirectWrite.TextFormat(_dwFactory, "Segoe UI", 12.0F) With {.TextAlignment = DirectWrite.TextAlignment.Center, .ParagraphAlignment = DirectWrite.ParagraphAlignment.Center}
                _renderTarget.DrawText("ПЕРЕТАЩИТЕ МЕДИАФАЙЛЫ НА ТАЙМЛАЙН", tf, New RawRectangleF(0, 0, _currentWidth, _currentHeight), _brushText)
            End Using
            Return
        End If

        Dim vDuration As TimeSpan = GetVirtualDuration()
        If vDuration <= TimeSpan.Zero Then Return

        Dim visibleVirtStart As TimeSpan = If(_state.IsZoomed, PhysicalToVirtual(_state.ViewStart), TimeSpan.Zero)
        Dim visibleVirtEnd As TimeSpan = If(_state.IsZoomed, PhysicalToVirtual(_state.ViewEnd), vDuration)

        Dim trackCount As Integer = Math.Max(1, If(_state.Tracks IsNot Nothing, _state.Tracks.Count, 2))
        Dim trackHeight As Single = GetTimelineTrackHeight() / trackCount
        Dim visibleSlots As New HashSet(Of String)()

        If _state.Tracks IsNot Nothing Then
            For trackIdx As Integer = 0 To trackCount - 1
                If trackIdx >= _state.Tracks.Count Then Continue For

                Dim trackSnap = _state.Tracks(trackIdx)
                Dim trackTop As Single = _timelineHeight + (trackIdx * trackHeight)
                Dim trackBottom As Single = trackTop + trackHeight

                For Each clip In trackSnap.Clips
                    If clip.TimelineEnd <= visibleVirtStart OrElse clip.TimelineStart >= visibleVirtEnd Then Continue For

                    Dim clipStartX As Single = CSng(GetPhysicalXFromTime(clip.TimelineStart))
                    Dim clipEndX As Single = CSng(GetPhysicalXFromTime(clip.TimelineEnd))
                    Dim clipRectLeft As Single = Math.Max(clipStartX, Padding)
                    Dim clipRectRight As Single = Math.Min(clipEndX, _currentWidth - Padding)

                    If clipRectRight <= clipRectLeft Then Continue For

                    Dim clipRect As New RawRectangleF(clipRectLeft, trackTop + 2, clipRectRight, trackBottom - 2)
                    _renderTarget.FillRectangle(clipRect, _brushPlaceholder)

                    ' ЕСЛИ ЭТО ТРЕК С ВИДЕО -> РИСУЕМ ЭСКИЗЫ (THUMBNAILS)
                    If trackSnap.Type = TargetFormatType.Video AndAlso clip.MediaType = TargetFormatType.Video Then
                        Dim cache As GpuFrameCacheManager = Nothing

                        If _caches.TryGetValue(clip.FilePath, cache) Then
                            Dim value As GpuFrameExtractor = Nothing
                            Dim extractor = If(_extractors.TryGetValue(clip.FilePath, value), value, Nothing)
                            Dim aspect As Double = If(cache.SlotWidth > 0 AndAlso cache.SlotHeight > 0, CDbl(cache.SlotWidth) / cache.SlotHeight, 16.0 / 9.0)
                            Dim idealThumbWidth As Single = CSng((trackHeight - 4) * aspect)

                            If idealThumbWidth < 1.0F Then idealThumbWidth = 1.0F

                            Dim fullClipWidth As Single = clipEndX - clipStartX
                            Dim clipThumbCount As Integer = Math.Max(1, CInt(Math.Ceiling(fullClipWidth / idealThumbWidth)))
                            Dim actualThumbWidth As Single = fullClipWidth / clipThumbCount

                            _renderTarget.PushAxisAlignedClip(clipRect, AntialiasMode.Aliased)
                            Try
                                For i As Integer = 0 To clipThumbCount - 1

                                    ' ==========================================================
                                    ' ОПТИМИЗАЦИЯ ДЛЯ СЛАБЫХ ПК: Фильтр отображения кадров
                                    ' ==========================================================
                                    Dim displayMode As Integer = 0
                                    Try
                                        displayMode = SettingsService.Instance.Current.TimelineThumbMode
                                    Catch
                                    End Try

                                    If displayMode = 1 Then ' Режим 1: Только Начало и Конец
                                        If i > 0 AndAlso i < clipThumbCount - 1 Then Continue For
                                    ElseIf displayMode = 2 Then ' Режим 2: Только Начало
                                        If i > 0 Then Continue For
                                    End If
                                    ' ==========================================================

                                    Dim thumbLeft As Single = clipStartX + i * actualThumbWidth
                                    Dim thumbRight As Single = thumbLeft + actualThumbWidth

                                    If thumbRight < Padding OrElse thumbLeft > _currentWidth - Padding Then Continue For

                                    Dim thumbTimeOffset As TimeSpan = TimeSpan.FromSeconds((i + 0.5) / clipThumbCount * (clip.SourceOut - clip.SourceIn).TotalSeconds)
                                    Dim targetSourceTime As TimeSpan = clip.SourceIn + thumbTimeOffset
                                    Dim slotIndex As Integer = CInt(Math.Floor(targetSourceTime.TotalSeconds / cache.SlotInterval.TotalSeconds))

                                    If slotIndex < 0 Then slotIndex = 0
                                    If slotIndex >= cache.TotalSlots Then slotIndex = cache.TotalSlots - 1

                                    Dim cacheKey As String = $"{clip.Id}_{slotIndex}"
                                    visibleSlots.Add(cacheKey)

                                    Dim gpuFrame = cache.GetFrame(slotIndex)
                                    If gpuFrame IsNot Nothing AndAlso gpuFrame.Texture IsNot Nothing Then
                                        Dim thumb As RenderThumbnail = Nothing
                                        If _thumbnails.TryGetValue(cacheKey, thumb) Then
                                            If Math.Abs(thumb.PtsMs - gpuFrame.PtsMs) > 1.0 Then
                                                thumb.Dispose()
                                                thumb = Nothing
                                            End If
                                        End If

                                        If thumb Is Nothing Then
                                            thumb = New RenderThumbnail() With {.PtsMs = gpuFrame.PtsMs}
                                            Dim desc = gpuFrame.Texture.Description
                                            desc.BindFlags = D3D11.BindFlags.ShaderResource Or D3D11.BindFlags.RenderTarget
                                            thumb.D3DTexture = New D3D11.Texture2D(_d3dDevice, desc)
                                            _d3dDevice.ImmediateContext.CopyResource(gpuFrame.Texture, thumb.D3DTexture)

                                            Using surface = thumb.D3DTexture.QueryInterface(Of DXGI.Surface)()
                                                Dim bmpProps As New BitmapProperties(New PixelFormat(DXGI.Format.B8G8R8A8_UNorm, AlphaMode.Ignore))
                                                thumb.D2DBitmap = New Direct2D.Bitmap(_renderTarget, surface, bmpProps)
                                            End Using
                                            _thumbnails(cacheKey) = thumb
                                        End If

                                        Dim destRect As New RawRectangleF(thumbLeft, trackTop + 2, thumbRight, trackBottom - 2)
                                        Dim srcRect As New RawRectangleF(0, 0, thumb.D2DBitmap.PixelSize.Width, thumb.D2DBitmap.PixelSize.Height)
                                        _renderTarget.DrawBitmap(thumb.D2DBitmap, destRect, 1.0F, FrameInterpolation, srcRect)
                                    Else
                                        If extractor IsNot Nothing AndAlso Not _activeFrameTasks.ContainsKey(cacheKey) Then
                                            _activeFrameTasks.TryAdd(cacheKey, True)
                                            Dim cts As New CancellationTokenSource()
                                            _cancellationTokens(cacheKey) = cts
                                            Dim loadTask As Task = extractor.EnsureFrameCachedAsync(slotIndex, cts.Token)

                                            loadTask.ContinueWith(Sub(t)
                                                                      Dim dummy As Boolean
                                                                      _activeFrameTasks.TryRemove(cacheKey, dummy)
                                                                      Dim dummyCts As CancellationTokenSource = Nothing
                                                                      If _cancellationTokens.TryRemove(cacheKey, dummyCts) AndAlso dummyCts IsNot Nothing Then
                                                                          dummyCts.Dispose()
                                                                      End If
                                                                  End Sub)
                                        End If
                                    End If
                                Next
                            Finally
                                _renderTarget.PopAxisAlignedClip()
                            End Try
                        End If

                        ' ЕСЛИ ЭТО ТРЕК С АУДИО -> РИСУЕМ ВЕЙВФОРМЫ
                    ElseIf trackSnap.Type = TargetFormatType.Audio AndAlso clip.MediaType = TargetFormatType.Audio Then
                        DrawAudioWaveform(clip, clipRect, clipStartX, clipEndX)
                    End If

                    ' Рисуем рамку и имя файла для всех типов клипов
                    If _selectedClip IsNot Nothing AndAlso clip.Id = _selectedClip.Id Then
                        _renderTarget.DrawRectangle(clipRect, _brushPlaybackLine, 2.5F)
                    Else
                        _renderTarget.DrawRectangle(clipRect, _brushLine, 1.5F)
                    End If

                    Using tf As New DirectWrite.TextFormat(_dwFactory, "Segoe UI", 10.0F) With {.WordWrapping = DirectWrite.WordWrapping.NoWrap}
                        Dim fileName = IO.Path.GetFileName(clip.FilePath)
                        _renderTarget.DrawText(fileName, tf, New RawRectangleF(clipRectLeft + 5, trackTop + 5, clipRectRight, trackBottom), _brushText)
                    End Using

                Next
            Next
        End If

        ' Отрисовка призрака (Drag & Drop)
        If _isDraggingClip AndAlso _draggedClipOriginal IsNot Nothing AndAlso _state.Tracks IsNot Nothing Then
            Dim ghostTrackIdx As Integer = -1

            For i As Integer = 0 To _state.Tracks.Count - 1
                If _state.Tracks(i).Id = _ghostTrackId Then
                    ghostTrackIdx = i
                    Exit For
                End If
            Next

            If ghostTrackIdx >= 0 Then
                Dim trackTop As Single = _timelineHeight + (ghostTrackIdx * trackHeight)
                Dim trackBottom As Single = trackTop + trackHeight

                Dim clipDur = _draggedClipOriginal.TimelineEnd - _draggedClipOriginal.TimelineStart
                Dim ghostEndTime = _ghostStartTime + clipDur

                Dim ghostStartX As Single = CSng(GetPhysicalXFromTime(_ghostStartTime))
                Dim ghostEndX As Single = CSng(GetPhysicalXFromTime(ghostEndTime))

                Dim ghostRectLeft As Single = Math.Max(ghostStartX, Padding)
                Dim ghostRectRight As Single = Math.Min(ghostEndX, _currentWidth - Padding)

                If ghostRectRight > ghostRectLeft Then
                    Dim ghostRect As New RawRectangleF(ghostRectLeft, trackTop + 2, ghostRectRight, trackBottom - 2)
                    Dim ghostBrush = If(_isGhostDropValid, _brushGhostValid, _brushGhostInvalid)

                    _renderTarget.FillRectangle(ghostRect, ghostBrush)
                    _renderTarget.DrawRectangle(ghostRect, _brushText, 1.5F)
                End If
            End If
        End If

        ' Очистка памяти для кадров вне видимой зоны
        Dim thumbKeysToRemove = _thumbnails.Keys.Where(Function(k) Not visibleSlots.Contains(k)).ToList()
        For Each key In thumbKeysToRemove
            _thumbnails(key).Dispose()
            _thumbnails.Remove(key)
        Next

        Dim keysToRemove = _cancellationTokens.Keys.Where(Function(k) Not visibleSlots.Contains(k)).ToList()
        For Each key In keysToRemove
            Dim cts As CancellationTokenSource = Nothing
            If _cancellationTokens.TryRemove(key, cts) Then
                Try
                    cts.Cancel()
                    cts.Dispose()
                Catch
                End Try
            End If
        Next
    End Sub

    Private Sub DrawTrackEnvelopes()
        DrawVideoFadesD2D()

        If Not IsAudioExpected Then Return

        Dim audRect = GetAudioTrackRect()
        Dim audioH As Single = audRect.Bottom - audRect.Top
        Dim audioTop As Single = audRect.Top
        Dim audioBottom As Single = audRect.Bottom
        Dim centerY As Single = audioTop + (audioH / 2.0F)

        Dim virtStart As TimeSpan = PhysicalToVirtual(_state.MarkerStart)
        Dim virtEnd As TimeSpan = PhysicalToVirtual(_state.MarkerEnd)

        Dim maxFadeSec As Double = (virtEnd - virtStart).TotalSeconds / 2.0
        Dim actualFadeInSec As Double = Math.Min(_audioFadeIn.TotalSeconds, maxFadeSec)
        Dim actualFadeOutSec As Double = Math.Min(_audioFadeOut.TotalSeconds, maxFadeSec)

        Dim virtFadeIn As TimeSpan = virtStart + TimeSpan.FromSeconds(actualFadeInSec)
        Dim virtFadeOut As TimeSpan = virtEnd - TimeSpan.FromSeconds(actualFadeOutSec)

        Dim markerStartX As Single = CSng(GetPhysicalXFromTime(virtStart))
        Dim markerEndX As Single = CSng(GetPhysicalXFromTime(virtEnd))
        Dim fadeInX As Single = CSng(GetPhysicalXFromTime(virtFadeIn))
        Dim fadeOutX As Single = CSng(GetPhysicalXFromTime(virtFadeOut))

        If fadeInX > fadeOutX Then
            Dim mid As Single = (fadeInX + fadeOutX) / 2.0F
            fadeInX = mid
            fadeOutX = mid
        End If

        Dim safeVol As Single = Math.Max(0.0F, Math.Min(1.0F, TrackVolume))
        Dim envelopeY As Single = centerY - (audioH / 2.0F) * safeVol
        Dim envelopeBottomY As Single = centerY + (audioH / 2.0F) * safeVol

        Dim Y_Start As Single = centerY
        Dim Y_FI As Single = envelopeY
        Dim Y_FO As Single = envelopeY
        Dim Y_End As Single = centerY

        Dim Yb_Start As Single = centerY
        Dim Yb_FI As Single = envelopeBottomY
        Dim Yb_FO As Single = envelopeBottomY
        Dim Yb_End As Single = centerY

        Using maskBrush As New SolidColorBrush(_renderTarget, New RawColor4(0.0F, 0.0F, 0.0F, 0.65F))
            Using pathGeom As New PathGeometry(_factory)
                Using sink As GeometrySink = pathGeom.Open()
                    sink.BeginFigure(New RawVector2(markerStartX, audioTop), FigureBegin.Filled)
                    sink.AddLine(New RawVector2(markerEndX, audioTop))
                    sink.AddLine(New RawVector2(markerEndX, Y_End))

                    If _audioFadeOut > TimeSpan.Zero AndAlso fadeOutX < markerEndX Then
                        Dim cp1 As New RawVector2(markerEndX - (markerEndX - fadeOutX) * 0.2F, Y_End)
                        Dim cp2 As New RawVector2(fadeOutX + (markerEndX - fadeOutX) * 0.5F, Y_FO)
                        sink.AddBezier(New BezierSegment() With {.Point1 = cp1, .Point2 = cp2, .Point3 = New RawVector2(fadeOutX, Y_FO)})
                    Else
                        sink.AddLine(New RawVector2(fadeOutX, Y_FO))
                    End If

                    sink.AddLine(New RawVector2(fadeInX, Y_FI))

                    If _audioFadeIn > TimeSpan.Zero AndAlso fadeInX > markerStartX Then
                        Dim cp1 As New RawVector2(fadeInX - (fadeInX - markerStartX) * 0.5F, Y_FI)
                        Dim cp2 As New RawVector2(markerStartX + (fadeInX - markerStartX) * 0.2F, Y_Start)
                        sink.AddBezier(New BezierSegment() With {.Point1 = cp1, .Point2 = cp2, .Point3 = New RawVector2(markerStartX, Y_Start)})
                    Else
                        sink.AddLine(New RawVector2(markerStartX, Y_Start))
                    End If
                    sink.EndFigure(FigureEnd.Closed)

                    sink.BeginFigure(New RawVector2(markerStartX, audioBottom), FigureBegin.Filled)
                    sink.AddLine(New RawVector2(markerEndX, audioBottom))
                    sink.AddLine(New RawVector2(markerEndX, Yb_End))

                    If _audioFadeOut > TimeSpan.Zero AndAlso fadeOutX < markerEndX Then
                        Dim cp1 As New RawVector2(markerEndX - (markerEndX - fadeOutX) * 0.2F, Yb_End)
                        Dim cp2 As New RawVector2(fadeOutX + (markerEndX - fadeOutX) * 0.5F, Yb_FO)
                        sink.AddBezier(New BezierSegment() With {.Point1 = cp1, .Point2 = cp2, .Point3 = New RawVector2(fadeOutX, Yb_FO)})
                    Else
                        sink.AddLine(New RawVector2(fadeOutX, Yb_FO))
                    End If

                    sink.AddLine(New RawVector2(fadeInX, Yb_FI))

                    If _audioFadeIn > TimeSpan.Zero AndAlso fadeInX > markerStartX Then
                        Dim cp1 As New RawVector2(fadeInX - (fadeInX - markerStartX) * 0.5F, Yb_FI)
                        Dim cp2 As New RawVector2(markerStartX + (fadeInX - markerStartX) * 0.2F, Yb_Start)
                        sink.AddBezier(New BezierSegment() With {.Point1 = cp1, .Point2 = cp2, .Point3 = New RawVector2(markerStartX, Yb_Start)})
                    Else
                        sink.AddLine(New RawVector2(markerStartX, Yb_Start))
                    End If
                    sink.EndFigure(FigureEnd.Closed)

                    sink.Close()
                End Using
                _renderTarget.FillGeometry(pathGeom, maskBrush)
            End Using
        End Using

        Using envLineBrush As New SolidColorBrush(_renderTarget, New RawColor4(1.0F, 1.0F, 1.0F, 1.0F))
            If _audioFadeIn > TimeSpan.Zero AndAlso fadeInX > markerStartX Then
                Using geom As New PathGeometry(_factory)
                    Using sink As GeometrySink = geom.Open()
                        sink.BeginFigure(New RawVector2(markerStartX, Y_Start), FigureBegin.Hollow)
                        Dim cp1 As New RawVector2(markerStartX + (fadeInX - markerStartX) * 0.2F, Y_Start)
                        Dim cp2 As New RawVector2(fadeInX - (fadeInX - markerStartX) * 0.5F, Y_FI)
                        sink.AddBezier(New BezierSegment() With {.Point1 = cp1, .Point2 = cp2, .Point3 = New RawVector2(fadeInX, Y_FI)})
                        sink.EndFigure(FigureEnd.Open)
                        sink.Close()
                    End Using
                    _renderTarget.DrawGeometry(geom, envLineBrush, 2.0F)
                End Using
            End If

            If fadeOutX > fadeInX Then
                _renderTarget.DrawLine(New RawVector2(fadeInX, Y_FI), New RawVector2(fadeOutX, Y_FO), envLineBrush, 2.0F)
            End If

            If _audioFadeOut > TimeSpan.Zero AndAlso fadeOutX < markerEndX Then
                Using geom As New PathGeometry(_factory)
                    Using sink As GeometrySink = geom.Open()
                        sink.BeginFigure(New RawVector2(fadeOutX, Y_FO), FigureBegin.Hollow)
                        Dim cp1 As New RawVector2(fadeOutX + (markerEndX - fadeOutX) * 0.5F, Y_FO)
                        Dim cp2 As New RawVector2(markerEndX - (markerEndX - fadeOutX) * 0.2F, Y_End)
                        sink.AddBezier(New BezierSegment() With {.Point1 = cp1, .Point2 = cp2, .Point3 = New RawVector2(markerEndX, Y_End)})
                        sink.EndFigure(FigureEnd.Open)
                        sink.Close()
                    End Using
                    _renderTarget.DrawGeometry(geom, envLineBrush, 2.0F)
                End Using
            End If

            Dim cornerSize As Single = 9.0F
            Dim fiX As Single = If(_audioFadeIn > TimeSpan.Zero, fadeInX, markerStartX)
            Using cornerGeom As New PathGeometry(_factory)
                Using sink As GeometrySink = cornerGeom.Open()
                    sink.BeginFigure(New RawVector2(fiX, Y_FI), FigureBegin.Filled)
                    sink.AddLine(New RawVector2(fiX - cornerSize, Y_FI))
                    sink.AddLine(New RawVector2(fiX, Y_FI + cornerSize))
                    sink.EndFigure(FigureEnd.Closed)
                    sink.Close()
                End Using
                _renderTarget.FillGeometry(cornerGeom, envLineBrush)
            End Using

            Dim foX As Single = If(_audioFadeOut > TimeSpan.Zero, fadeOutX, markerEndX)
            Using cornerGeom As New PathGeometry(_factory)
                Using sink As GeometrySink = cornerGeom.Open()
                    sink.BeginFigure(New RawVector2(foX, Y_FO), FigureBegin.Filled)
                    sink.AddLine(New RawVector2(foX + cornerSize, Y_FO))
                    sink.AddLine(New RawVector2(foX, Y_FO + cornerSize))
                    sink.EndFigure(FigureEnd.Closed)
                    sink.Close()
                End Using
                _renderTarget.FillGeometry(cornerGeom, envLineBrush)
            End Using
        End Using
    End Sub

    Private Sub DrawVideoFadesD2D()
        If _state Is Nothing Then Return

        Dim virtStart As TimeSpan = PhysicalToVirtual(_state.MarkerStart)
        Dim virtEnd As TimeSpan = PhysicalToVirtual(_state.MarkerEnd)
        Dim startX As Single = CSng(GetPhysicalXFromTime(virtStart))
        Dim endX As Single = CSng(GetPhysicalXFromTime(virtEnd))

        Dim maxFadeSec As Double = (virtEnd - virtStart).TotalSeconds / 2.0
        Dim actualFadeInSec As Double = Math.Min(_videoFadeIn.TotalSeconds, maxFadeSec)
        Dim actualFadeOutSec As Double = Math.Min(_videoFadeOut.TotalSeconds, maxFadeSec)

        Dim fadeInX As Single = CSng(GetPhysicalXFromTime(virtStart + TimeSpan.FromSeconds(actualFadeInSec)))
        Dim fadeOutX As Single = CSng(GetPhysicalXFromTime(virtEnd - TimeSpan.FromSeconds(actualFadeOutSec)))

        If fadeInX > fadeOutX Then
            Dim mid As Single = (fadeInX + fadeOutX) / 2.0F
            fadeInX = mid
            fadeOutX = mid
        End If

        Dim trackCount As Integer = Math.Max(1, If(_projectModel IsNot Nothing, _projectModel.Tracks.Count, 2))
        Dim trackHeight As Single = GetTimelineTrackHeight() / trackCount
        Dim topY As Single = _timelineHeight
        Dim bottomY As Single = _timelineHeight + trackHeight

        If actualFadeInSec > 0 AndAlso fadeInX > startX Then
            Using geometry As New PathGeometry(_factory)
                Using sink As GeometrySink = geometry.Open()
                    sink.BeginFigure(New RawVector2(startX, bottomY), FigureBegin.Filled)
                    AddFadeCurve(sink, New RawVector2(startX, bottomY), New RawVector2(fadeInX, topY), _VideoFadeInType)
                    sink.AddLine(New RawVector2(startX, topY))
                    sink.EndFigure(FigureEnd.Closed)
                    sink.Close()
                End Using
                _renderTarget.FillGeometry(geometry, _brushDim)
            End Using

            Using lineGeo As New PathGeometry(_factory)
                Using lineSink As GeometrySink = lineGeo.Open()
                    lineSink.BeginFigure(New RawVector2(startX, bottomY), FigureBegin.Hollow)
                    AddFadeCurve(lineSink, New RawVector2(startX, bottomY), New RawVector2(fadeInX, topY), _VideoFadeInType)
                    lineSink.EndFigure(FigureEnd.Open)
                    lineSink.Close()
                End Using
                _renderTarget.DrawGeometry(lineGeo, _brushFadeLine, 1.5F)
            End Using
        End If

        If actualFadeOutSec > 0 AndAlso fadeOutX < endX Then
            Using geometry As New PathGeometry(_factory)
                Using sink As GeometrySink = geometry.Open()
                    sink.BeginFigure(New RawVector2(fadeOutX, topY), FigureBegin.Filled)
                    AddFadeCurve(sink, New RawVector2(fadeOutX, topY), New RawVector2(endX, bottomY), _VideoFadeOutType)
                    sink.AddLine(New RawVector2(endX, topY))
                    sink.EndFigure(FigureEnd.Closed)
                    sink.Close()
                End Using
                _renderTarget.FillGeometry(geometry, _brushDim)
            End Using

            Using lineGeo As New PathGeometry(_factory)
                Using lineSink As GeometrySink = lineGeo.Open()
                    lineSink.BeginFigure(New RawVector2(fadeOutX, topY), FigureBegin.Hollow)
                    AddFadeCurve(lineSink, New RawVector2(fadeOutX, topY), New RawVector2(endX, bottomY), _VideoFadeOutType)
                    lineSink.EndFigure(FigureEnd.Open)
                    lineSink.Close()
                End Using
                _renderTarget.DrawGeometry(lineGeo, _brushFadeLine, 1.5F)
            End Using
        End If

        DrawFadeHandleD2D(startX, topY, True, _brushFadeHandle)
        DrawFadeHandleD2D(endX, topY, False, _brushFadeHandle)
    End Sub

    Private Shared Sub AddFadeCurve(sink As GeometrySink, startPoint As RawVector2, endPoint As RawVector2, fadeType As VegasFadeType)
        Dim cp1, cp2 As RawVector2
        Dim w As Single = endPoint.X - startPoint.X
        Dim h As Single = endPoint.Y - startPoint.Y

        Select Case fadeType
            Case VegasFadeType.Fast
                cp1 = New RawVector2(startPoint.X, endPoint.Y)
                cp2 = New RawVector2(startPoint.X + w * 0.3F, endPoint.Y)
            Case VegasFadeType.Slow
                cp1 = New RawVector2(startPoint.X + w * 0.7F, startPoint.Y)
                cp2 = New RawVector2(endPoint.X, startPoint.Y)
            Case VegasFadeType.Smooth
                cp1 = New RawVector2(startPoint.X + w * 0.5F, startPoint.Y)
                cp2 = New RawVector2(endPoint.X - w * 0.5F, endPoint.Y)
            Case VegasFadeType.Sharp
                cp1 = New RawVector2(startPoint.X, startPoint.Y + h * 0.5F)
                cp2 = New RawVector2(endPoint.X, endPoint.Y - h * 0.5F)
            Case Else
                cp1 = New RawVector2(startPoint.X + w * 0.333F, startPoint.Y + h * 0.333F)
                cp2 = New RawVector2(startPoint.X + w * 0.666F, startPoint.Y + h * 0.666F)
        End Select
        sink.AddBezier(New BezierSegment() With {.Point1 = cp1, .Point2 = cp2, .Point3 = endPoint})
    End Sub

    Private Sub DrawFadeHandleD2D(x As Single, y As Single, isLeft As Boolean, brush As SolidColorBrush)
        Dim handleSize As Single = 12.0F
        Using geometry As New PathGeometry(_factory)
            Using sink As GeometrySink = geometry.Open()
                If isLeft Then
                    sink.BeginFigure(New RawVector2(x, y), FigureBegin.Filled)
                    sink.AddLine(New RawVector2(x + handleSize, y))
                    sink.AddArc(New ArcSegment() With {
                        .Point = New RawVector2(x, y + handleSize),
                        .Size = New SharpDX.Size2F(handleSize, handleSize),
                        .SweepDirection = SweepDirection.Clockwise,
                        .ArcSize = ArcSize.Small
                    })
                    sink.EndFigure(FigureEnd.Closed)
                Else
                    sink.BeginFigure(New RawVector2(x, y), FigureBegin.Filled)
                    sink.AddLine(New RawVector2(x - handleSize, y))
                    sink.AddArc(New ArcSegment() With {
                        .Point = New RawVector2(x, y + handleSize),
                        .Size = New SharpDX.Size2F(handleSize, handleSize),
                        .SweepDirection = SweepDirection.Clockwise,
                        .ArcSize = ArcSize.Small
                    })
                    sink.EndFigure(FigureEnd.Closed)
                End If
                sink.Close()
            End Using
            _renderTarget.FillGeometry(geometry, brush)
        End Using
    End Sub

    Private Sub DrawOverlays()
        If _hasSelection AndAlso _state.MarkerStart >= TimeSpan.Zero AndAlso _state.MarkerEnd > _state.MarkerStart AndAlso _state.MarkerEnd <= _state.Duration Then
            Dim virtStart As TimeSpan = PhysicalToVirtual(_state.MarkerStart)
            Dim virtEnd As TimeSpan = PhysicalToVirtual(_state.MarkerEnd)
            Dim startX As Integer = GetPhysicalXFromTime(virtStart)
            Dim endX As Integer = GetPhysicalXFromTime(virtEnd)

            If startX < endX Then
                Dim targetAlpha As Single = If(_isMediaPlaying, 0.2F, 0.4F)
                _brushSelection.Color = New RawColor4(0 / 255.0F, 120 / 255.0F, 215 / 255.0F, targetAlpha)
                _renderTarget.FillRectangle(New RawRectangleF(startX, _timelineHeight, endX, _contentHeightWithoutScroll), _brushSelection)
                _renderTarget.FillRectangle(New RawRectangleF(startX, 0.0F, endX, _timelineHeight), _brushAudioCenterLine)
            End If
        End If

        If _state.CutRegions IsNot Nothing Then
            For Each cut In _state.CutRegions
                Dim cutVirtTime As TimeSpan = PhysicalToVirtual(cut.StartTime)
                Dim cutX As Integer = GetPhysicalXFromTime(cutVirtTime)
                If cutX >= -50 AndAlso cutX <= _currentWidth + 50 Then
                    _renderTarget.DrawLine(New RawVector2(cutX, _timelineHeight), New RawVector2(cutX, _contentHeightWithoutScroll), _brushSplice, 1.5F)
                End If
            Next
        End If

        If _state.Duration > TimeSpan.Zero Then DrawTicks()

        If _hasSelection Then
            DrawTimelineSelectionMarker(GetPhysicalXFromTime(PhysicalToVirtual(_state.MarkerStart)), _contentHeightWithoutScroll, True)
            DrawTimelineSelectionMarker(GetPhysicalXFromTime(PhysicalToVirtual(_state.MarkerEnd)), _contentHeightWithoutScroll, False)
        End If

        If _activeSnapTime.HasValue Then
            Dim snapX As Integer = GetPhysicalXFromTime(_activeSnapTime.Value)
            If snapX >= Padding AndAlso snapX <= _currentWidth - Padding Then
                _renderTarget.DrawLine(New RawVector2(snapX, _timelineHeight), New RawVector2(snapX, _contentHeightWithoutScroll), _brushMarkerAccent, 1.5F)
            End If
        End If

        If _currentPlaybackX <> -1 Then
            DrawProfessionalPlayhead(_currentPlaybackX, _contentHeightWithoutScroll)
        End If

        If _currentMouseX >= 0 AndAlso _currentMouseX < _currentWidth AndAlso Not String.IsNullOrEmpty(_hoverTimeStr) Then
            Using tf As New DirectWrite.TextFormat(_dwFactory, "Segoe UI", 10.0F) With {.TextAlignment = DirectWrite.TextAlignment.Leading, .ParagraphAlignment = DirectWrite.ParagraphAlignment.Near}
                Using layout As New DirectWrite.TextLayout(_dwFactory, _hoverTimeStr, tf, 1000.0F, 100.0F)
                    Dim rectW As Single = layout.Metrics.Width + 12.0F
                    Dim rectH As Single = layout.Metrics.Height + 8.0F
                    Dim rectX As Single = If(_currentMouseX + 12.0F + rectW > _currentWidth - 2.0F, _currentMouseX - rectW - 12.0F, CSng(_currentMouseX + 12.0F))
                    If rectX < 0 Then rectX = 0
                    Dim rectY As Single = _timelineHeight + 5.0F
                    _renderTarget.FillRectangle(New RawRectangleF(rectX, rectY, rectX + rectW, rectY + rectH), _brushTooltipBg)
                    _renderTarget.DrawRectangle(New RawRectangleF(rectX, rectY, rectX + rectW, rectY + rectH), _brushTooltipBorder, 1.0F)
                    _renderTarget.DrawTextLayout(New RawVector2(rectX + 6.0F, rectY + 4.0F), layout, _brushText)
                End Using
            End Using
        End If
    End Sub

    Private Sub DrawVolumeOverlay()
        If Not Me.IsAudioExpected Then Return
        If _audioTrackRect.Bottom - _audioTrackRect.Top <= 0 Then Return

        Dim tooltipText As String = ""

        If _isDraggingVolume AndAlso _currentMouseX >= 0 AndAlso _currentMouseY >= 0 Then
            Dim volPercentage As Integer = CInt(Me.TrackVolume * 100)
            tooltipText = $"Громкость: {volPercentage}%"
        ElseIf _interaction = TimelineInteractionState.DraggingFadeIn AndAlso _currentMouseX >= 0 AndAlso _currentMouseY >= 0 Then
            tooltipText = $"Аудио Нарастание: {_audioFadeIn.TotalSeconds:F2} с"
        ElseIf _interaction = TimelineInteractionState.DraggingFadeOut AndAlso _currentMouseX >= 0 AndAlso _currentMouseY >= 0 Then
            tooltipText = $"Аудио Затухание: {_audioFadeOut.TotalSeconds:F2} с"
        ElseIf _interaction = TimelineInteractionState.DraggingVideoFadeIn AndAlso _currentMouseX >= 0 AndAlso _currentMouseY >= 0 Then
            tooltipText = $"Видео Нарастание: {_videoFadeIn.TotalSeconds:F2} с"
        ElseIf _interaction = TimelineInteractionState.DraggingVideoFadeOut AndAlso _currentMouseX >= 0 AndAlso _currentMouseY >= 0 Then
            tooltipText = $"Видео Затухание: {_videoFadeOut.TotalSeconds:F2} с"
        ElseIf _interaction = TimelineInteractionState.DraggingVideoFadeInCurve Then
            tooltipText = $"Кривая: {_VideoFadeInType}"
        ElseIf _interaction = TimelineInteractionState.DraggingVideoFadeOutCurve Then
            tooltipText = $"Кривая: {_VideoFadeOutType}"
        End If

        If Not String.IsNullOrEmpty(tooltipText) Then
            Using tf As New DirectWrite.TextFormat(_dwFactory, "Segoe UI", 10.0F) With {
                .TextAlignment = DirectWrite.TextAlignment.Leading,
                .ParagraphAlignment = DirectWrite.ParagraphAlignment.Near
            }
                Using layout As New DirectWrite.TextLayout(_dwFactory, tooltipText, tf, 200.0F, 50.0F)
                    Dim rectW As Single = layout.Metrics.Width + 10.0F
                    Dim rectH As Single = layout.Metrics.Height + 6.0F

                    Dim rectX As Single = _currentMouseX + 15.0F
                    Dim rectY As Single = _currentMouseY + 15.0F

                    If rectX + rectW > _currentWidth Then rectX = _currentMouseX - rectW - 10.0F
                    If rectY + rectH > _currentHeight Then rectY = _currentMouseY - rectH - 10.0F
                    If rectX < 0 Then rectX = 0
                    If rectY < 0 Then rectY = 0

                    Dim bgRect As New RawRectangleF(rectX, rectY, rectX + rectW, rectY + rectH)

                    Using bgBrush As New SolidColorBrush(_renderTarget, New RawColor4(255 / 255.0F, 255 / 255.0F, 225 / 255.0F, 1.0F))
                        _renderTarget.FillRectangle(bgRect, bgBrush)
                    End Using

                    Using borderBrush As New SolidColorBrush(_renderTarget, New RawColor4(100 / 255.0F, 100 / 255.0F, 100 / 255.0F, 1.0F))
                        _renderTarget.DrawRectangle(bgRect, borderBrush, 1.0F)
                    End Using

                    Using textBrush As New SolidColorBrush(_renderTarget, New RawColor4(0.0F, 0.0F, 0.0F, 1.0F))
                        _renderTarget.DrawTextLayout(New RawVector2(rectX + 5.0F, rectY + 3.0F), layout, textBrush)
                    End Using
                End Using
            End Using
        End If
    End Sub

    Private Sub DrawTicks()
        Dim lineY As Single = _timelineHeight
        _renderTarget.FillRectangle(New RawRectangleF(Padding, 0, _currentWidth - Padding, lineY), _brushOutline)
        _renderTarget.DrawLine(New RawVector2(Padding, lineY), New RawVector2(_currentWidth - Padding, lineY), _brushLine, 1.0F)

        Dim vDuration As TimeSpan = GetVirtualDuration()
        Dim visibleStartSec As Double = If(_state.IsZoomed, PhysicalToVirtual(_state.ViewStart).TotalSeconds, 0)
        Dim visibleEndSec As Double = If(_state.IsZoomed, PhysicalToVirtual(_state.ViewEnd).TotalSeconds, vDuration.TotalSeconds)
        Dim visibleDur As Double = Math.Max(0.001, visibleEndSec - visibleStartSec)

        Dim pixelsPerSecond As Double = _totalTimelineWidth / vDuration.TotalSeconds
        If pixelsPerSecond <= 0.0001 Then Return

        Dim targetSpacingPx As Double = 90.0
        Dim rawInterval As Double = targetSpacingPx / pixelsPerSecond
        Dim mainTickInterval As Double
        Dim subDivisions As Long

        If rawInterval < 1.0 AndAlso _fps > 0 Then
            Dim frameDuration As Double = 1.0 / _fps
            Dim rawFrames As Double = rawInterval / frameDuration
            If rawFrames <= 2.0 Then : mainTickInterval = frameDuration : subDivisions = 1
            ElseIf rawFrames <= 7.5 Then : mainTickInterval = frameDuration * 5.0 : subDivisions = 5
            ElseIf rawFrames <= 15.0 Then : mainTickInterval = frameDuration * 10.0 : subDivisions = 10
            Else : mainTickInterval = frameDuration * 15.0 : subDivisions = 5
            End If
        Else
            If rawInterval < 1.0 Then
                Dim mag As Double = Math.Pow(10, Math.Floor(Math.Log10(rawInterval)))
                Dim norm As Double = rawInterval / mag
                If norm < 1.5 Then : mainTickInterval = 1.0 * mag : subDivisions = 10
                ElseIf norm < 3.5 Then : mainTickInterval = 2.0 * mag : subDivisions = 4
                ElseIf norm < 7.5 Then : mainTickInterval = 5.0 * mag : subDivisions = 5
                Else : mainTickInterval = 10.0 * mag : subDivisions = 10
                End If
            ElseIf rawInterval < 60.0 Then
                If rawInterval < 1.5 Then : mainTickInterval = 1.0 : subDivisions = 10
                ElseIf rawInterval < 3.5 Then : mainTickInterval = 2.0 : subDivisions = 4
                ElseIf rawInterval < 7.5 Then : mainTickInterval = 5.0 : subDivisions = 5
                ElseIf rawInterval < 12.5 Then : mainTickInterval = 10.0 : subDivisions = 10
                ElseIf rawInterval < 22.5 Then : mainTickInterval = 15.0 : subDivisions = 5
                ElseIf rawInterval < 45.0 Then : mainTickInterval = 30.0 : subDivisions = 6
                Else : mainTickInterval = 60.0 : subDivisions = 6
                End If
            ElseIf rawInterval < 3600.0 Then
                Dim minInterval As Double = rawInterval / 60.0
                If minInterval < 1.5 Then : mainTickInterval = 60.0 : subDivisions = 12
                ElseIf minInterval < 3.5 Then : mainTickInterval = 120.0 : subDivisions = 4
                ElseIf minInterval < 7.5 Then : mainTickInterval = 300.0 : subDivisions = 6
                ElseIf minInterval < 12.5 Then : mainTickInterval = 600.0 : subDivisions = 12
                ElseIf minInterval < 22.5 Then : mainTickInterval = 900.0 : subDivisions = 6
                ElseIf minInterval < 45.0 Then : mainTickInterval = 1800.0 : subDivisions = 6
                Else : mainTickInterval = 3600.0 : subDivisions = 30
                End If
            Else
                Dim hrInterval As Double = rawInterval / 3600.0
                If hrInterval < 1.5 Then : mainTickInterval = 3600.0 : subDivisions = 12
                ElseIf hrInterval < 3.5 Then : mainTickInterval = 7200.0 : subDivisions = 4
                ElseIf hrInterval < 9.0 Then : mainTickInterval = 14400.0 : subDivisions = 12
                ElseIf hrInterval < 18.0 Then : mainTickInterval = 43200.0 : subDivisions = 5
                ElseIf hrInterval < 36.0 Then : mainTickInterval = 86400.0 : subDivisions = 6
                Else
                    Dim daysMag As Double = Math.Pow(10, Math.Floor(Math.Log10(hrInterval / 24.0)))
                    Dim norm As Double = (hrInterval / 24.0) / daysMag
                    If norm < 3.5 Then : mainTickInterval = 86400.0 * 2.0 * daysMag : subDivisions = 12
                    ElseIf norm < 7.5 Then : mainTickInterval = 86400.0 * 5.0 * daysMag : subDivisions = 10
                    Else : mainTickInterval = 86400.0 * 10.0 * daysMag : subDivisions = 20
                    End If
                End If
            End If
        End If

        Dim subTickInterval As Double = mainTickInterval / subDivisions
        Dim startTickIdx As Long = CLng(Math.Floor(visibleStartSec / subTickInterval))
        Dim endTickIdx As Long = CLng(Math.Ceiling(visibleEndSec / subTickInterval))

        Using tf As New DirectWrite.TextFormat(_dwFactory, "Segoe UI", 9.5F) With {
            .TextAlignment = DirectWrite.TextAlignment.Leading,
            .ParagraphAlignment = DirectWrite.ParagraphAlignment.Center
        }
            For tickIdx As Long = startTickIdx To endTickIdx
                Dim currentSec As Double = tickIdx * subTickInterval
                Dim xPos As Single = CSng(GetPhysicalXFromTime(TimeSpan.FromSeconds(currentSec)))

                If xPos >= Padding AndAlso xPos <= _currentWidth - Padding Then
                    If Math.Abs(tickIdx Mod subDivisions) = 0 Then
                        _renderTarget.DrawLine(New RawVector2(xPos, 0.0F), New RawVector2(xPos, lineY), _brushTickLong, 1.2F)
                        Dim timeStr As String = FormatTimecode(currentSec, _fps)
                        _renderTarget.DrawText(timeStr, tf, New RawRectangleF(xPos + 3.0F, 0.0F, xPos + 100.0F, lineY), _brushTickLong)
                    ElseIf Math.Abs(tickIdx Mod subDivisions) = subDivisions \ 2 Then
                        _renderTarget.DrawLine(New RawVector2(xPos, lineY - 7.0F), New RawVector2(xPos, lineY), _brushTickShort, 1.0F)
                    Else
                        _renderTarget.DrawLine(New RawVector2(xPos, lineY - 3.0F), New RawVector2(xPos, lineY), _brushTickShort, 0.5F)
                    End If
                End If
            Next
        End Using
    End Sub

    Private Sub DrawProfessionalPlayhead(x As Single, controlHeight As Single)
        ' 1. Рисуем линию поверх клипов тонкой (1 пиксель) и полупрозрачной (Alpha = 0.6)
        Dim originalColor = _brushPlaybackLine.Color
        _brushPlaybackLine.Color = New RawColor4(originalColor.R, originalColor.G, originalColor.B, 0.6F)

        _renderTarget.DrawLine(New RawVector2(x, _timelineHeight), New RawVector2(x, controlHeight), _brushPlaybackLine, 1.0F)

        ' Возвращаем нормальную яркость для "флажка"
        _brushPlaybackLine.Color = originalColor

        ' 2. Рисуем желтый "флажок" строго в пределах верхней линейки (до _timelineHeight)
        Using geometry As New PathGeometry(_factory)
            Using sink As GeometrySink = geometry.Open()
                sink.BeginFigure(New RawVector2(x - 6.0F, 0.0F), FigureBegin.Filled)
                sink.AddLine(New RawVector2(x + 6.0F, 0.0F))
                sink.AddLine(New RawVector2(x + 6.0F, _timelineHeight - 4.0F))
                sink.AddLine(New RawVector2(x, _timelineHeight))
                sink.AddLine(New RawVector2(x - 6.0F, _timelineHeight - 4.0F))
                sink.EndFigure(FigureEnd.Closed)
                sink.Close()
            End Using

            _renderTarget.FillGeometry(geometry, _brushPlaybackLine)
            _renderTarget.DrawGeometry(geometry, _brushPlayheadBorder, 1.0F)
        End Using
    End Sub

    Private Sub DrawTimelineSelectionMarker(x As Single, controlHeight As Single, isStartMarker As Boolean)
        Dim markerWidth As Single = 9.0F
        Dim shadowOffset As Single = If(isStartMarker, 1.0F, -1.0F)

        _renderTarget.DrawLine(New RawVector2(x + shadowOffset, _timelineHeight), New RawVector2(x + shadowOffset, controlHeight), _brushMarkerShadow, 1.0F)
        _renderTarget.DrawLine(New RawVector2(x, _timelineHeight), New RawVector2(x, controlHeight), _brushMarkerLine, 1.0F)

        Using geometry As New PathGeometry(_factory)
            Using sink As GeometrySink = geometry.Open()
                If isStartMarker Then
                    sink.BeginFigure(New RawVector2(x, 0.0F), FigureBegin.Filled)
                    sink.AddLine(New RawVector2(x + markerWidth, 0.0F))
                    sink.AddLine(New RawVector2(x + markerWidth, 4.0F))
                    sink.AddLine(New RawVector2(x + 4.0F, _timelineHeight))
                    sink.AddLine(New RawVector2(x, _timelineHeight))
                Else
                    sink.BeginFigure(New RawVector2(x, 0.0F), FigureBegin.Filled)
                    sink.AddLine(New RawVector2(x - markerWidth, 0.0F))
                    sink.AddLine(New RawVector2(x - markerWidth, 4.0F))
                    sink.AddLine(New RawVector2(x - 4.0F, _timelineHeight))
                    sink.AddLine(New RawVector2(x, _timelineHeight))
                End If
                sink.EndFigure(FigureEnd.Closed)
                sink.Close()
            End Using
            _renderTarget.FillGeometry(geometry, _brushMarkerFill)
            _renderTarget.DrawGeometry(geometry, _brushMarkerBorder, 1.0F)
            Dim ax As Single = If(isStartMarker, x + 2.0F, x - 3.0F)
            _renderTarget.FillRectangle(New RawRectangleF(ax, 2.0F, ax + 1.0F, 6.0F), _brushMarkerAccent)
        End Using
    End Sub

    Private Sub DrawScrollbar()
        If Not _isScrollbarVisible Then Return

        Dim trackRect As New RawRectangleF(Padding, _contentHeightWithoutScroll, _currentWidth - Padding, _currentHeight)
        _renderTarget.FillRectangle(trackRect, _brushOutline)

        Dim trackWidth As Single = trackRect.Right - trackRect.Left
        Dim thumbWidth As Single = Math.Max(ScrollbarMinThumbWidth, CSng(trackWidth * (_currentWidth / _totalTimelineWidth)))
        Dim maxScroll As Double = _totalTimelineWidth - _currentWidth
        Dim thumbPos As Single = trackRect.Left
        If maxScroll > 0 Then
            thumbPos += CSng((trackWidth - thumbWidth) * (_scrollOffset / maxScroll))
        End If

        Dim thumbRect As New RawRectangleF(thumbPos, trackRect.Top + ScrollbarPadding, thumbPos + thumbWidth, trackRect.Bottom - ScrollbarPadding)
        _renderTarget.FillRectangle(thumbRect, _brushText)
    End Sub

    Private Sub DrawLoadingAnimation()
        _renderTarget.Clear(_brushLoadingBg.Color)
        _renderTarget.Transform = MakeRotationTranslationMatrix(_rotAngle, _currentWidth / 2.0F, _currentHeight / 2.0F)
        For i As Integer = 3 To 1 Step -1
            _brushGlow.Color = New RawColor4(0.0F, 191 / 255.0F, 255 / 255.0F, (_fadeAlpha / 255.0F) * (40.0F / i) / 255.0F)
            _renderTarget.DrawGeometry(_loadingArcGeom, _brushGlow, 4.0F + i * 3.0F, New StrokeStyle(_factory, New StrokeStyleProperties() With {.StartCap = CapStyle.Round, .EndCap = CapStyle.Round, .DashCap = CapStyle.Round, .LineJoin = LineJoin.Round}))
        Next
        _brushMain.Color = New RawColor4(0.0F, 230 / 255.0F, 255 / 255.0F, _fadeAlpha / 255.0F)
        _renderTarget.DrawGeometry(_loadingArcGeom, _brushMain, 3.0F, New StrokeStyle(_factory, New StrokeStyleProperties() With {.StartCap = CapStyle.Round, .EndCap = CapStyle.Round, .DashCap = CapStyle.Round, .LineJoin = LineJoin.Round}))
        _renderTarget.Transform = IdentityMatrix
    End Sub

    Private Shared Function MakeRotationTranslationMatrix(angleDeg As Single, cx As Single, cy As Single) As RawMatrix3x2
        Dim rad As Double = angleDeg * Math.PI / 180.0
        Dim c As Single = CSng(Math.Cos(rad))
        Dim s As Single = CSng(Math.Sin(rad))
        Return New RawMatrix3x2(c, s, -s, c, cx, cy)
    End Function

    Public Sub Dispose() Implements IDisposable.Dispose
        Dispose(True)
        GC.SuppressFinalize(Me)
    End Sub

    Protected Overridable Sub Dispose(disposing As Boolean)
        If Not _disposed Then
            If disposing Then
                RemoveHandler ThemeManager.ThemeChanged, AddressOf OnThemeChanged
                If _boundControl IsNot Nothing Then
                    RemoveHandler _boundControl.Paint, AddressOf Control_Paint
                    RemoveHandler _boundControl.MouseDown, AddressOf Control_MouseDown
                    RemoveHandler _boundControl.MouseMove, AddressOf Control_MouseMove
                    RemoveHandler _boundControl.MouseUp, AddressOf Control_MouseUp
                    RemoveHandler _boundControl.MouseLeave, AddressOf Control_MouseLeave
                    RemoveHandler _boundControl.MouseWheel, AddressOf Control_MouseWheel
                    Dim picBox = TryCast(_boundControl, PictureBox)
                    If picBox IsNot Nothing Then
                        RemoveHandler picBox.MouseCaptureChanged, AddressOf Control_MouseCaptureChanged
                    End If
                    _boundControl = Nothing
                End If
                SyncLock _renderLock
                    For Each cache In _caches.Values
                        RemoveHandler cache.FrameCached, AddressOf OnFrameCached
                    Next
                    DiscardDeviceResources()
                End SyncLock
                If _factory IsNot Nothing Then _factory.Dispose()
                If _dwFactory IsNot Nothing Then _dwFactory.Dispose()
            End If
            _disposed = True
        End If
    End Sub
End Class