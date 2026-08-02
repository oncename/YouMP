' Path: TimelineController.vb
Option Strict On
Option Explicit On

Imports System
Imports System.Drawing
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports yoump.IServices

Public Class TimelineController
    Implements ITimelineController

    Public Event SeekRequested As Action(Of TimeSpan) Implements ITimelineController.SeekRequested
    Public Event PreviewRequested As Action(Of TimeSpan) Implements ITimelineController.PreviewRequested
    Public Event PlaybackPauseRequested As Action Implements ITimelineController.PlaybackPauseRequested
    Public Event AudioOffsetChanged As Action(Of TimeSpan) Implements ITimelineController.AudioOffsetChanged
    Public Event AudioOffsetCommit As Action(Of TimeSpan) Implements ITimelineController.AudioOffsetCommit
    Public Event MarkersChanged As Action Implements ITimelineController.MarkersChanged
    Public Event CursorMoved As Action Implements ITimelineController.CursorMoved
    Public Event MarkersCommitRequested As Action Implements ITimelineController.MarkersCommitRequested

    Private ReadOnly _renderer As ITimelineRenderer
    Private ReadOnly _model As ProjectModel
    Private _hoverVirtualTime As TimeSpan? = Nothing

    Private _lastFps As Double = 30.0
    Private _lastHasSelection As Boolean = False
    Private _lastIsAudioReplaced As Boolean = False
    Private _lastHasAudio As Boolean = False
    Private _lastAudioOffset As TimeSpan = TimeSpan.Zero
    Private _lastBakedAudioOffset As TimeSpan = TimeSpan.Zero

    Private _lastScrubTick As Long = 0
    Private Const SCRUB_INTERVAL_MS As Integer = 33

    Public Sub New(renderer As ITimelineRenderer, model As ProjectModel)
        _renderer = renderer
        _model = model

        AddHandler _renderer.PlayheadScrubbed, AddressOf OnPlayheadScrubbed
        AddHandler _renderer.PlayheadSeekCompleted, AddressOf OnPlayheadSeekCompleted
        AddHandler _renderer.MarkerStartChanged, AddressOf OnMarkerStartChanged
        AddHandler _renderer.MarkerEndChanged, AddressOf OnMarkerEndChanged
        AddHandler _renderer.MarkersCommit, AddressOf OnMarkersCommit
        AddHandler _renderer.AudioOffsetChanged, AddressOf OnAudioOffsetChanged
        AddHandler _renderer.AudioOffsetCommit, AddressOf OnAudioOffsetCommit
        AddHandler _renderer.PreviewRequested, AddressOf OnPreviewRequested
        AddHandler _renderer.PlaybackPauseRequested, AddressOf OnPlaybackPauseRequested
        AddHandler _renderer.CursorMoved, AddressOf OnCursorMoved
        AddHandler _renderer.CursorLeft, AddressOf OnCursorLeft
    End Sub

    Public ReadOnly Property HoverVirtualTime As TimeSpan? Implements ITimelineController.HoverVirtualTime
        Get
            Return _hoverVirtualTime
        End Get
    End Property

    Public Sub Initialize(pictureBox As PictureBox) Implements ITimelineController.Initialize
        _renderer.Initialize(pictureBox)
    End Sub

    Public Sub PushState(fps As Double, hasSelection As Boolean, isAudioReplaced As Boolean, hasAudio As Boolean, audioOffset As TimeSpan, bakedAudioOffset As TimeSpan) Implements ITimelineController.PushState
        _lastFps = fps : _lastHasSelection = hasSelection : _lastIsAudioReplaced = isAudioReplaced
        _lastHasAudio = hasAudio : _lastAudioOffset = audioOffset : _lastBakedAudioOffset = bakedAudioOffset
        InternalPushState()
    End Sub

    Private Sub InternalPushState()
        Dim stateData = _model.GetTimelineStateData()
        _renderer.UpdateState(stateData, _lastFps, _lastHasSelection, _lastIsAudioReplaced, _lastHasAudio)
        _renderer.UpdateAudioOffset(_lastAudioOffset, _lastBakedAudioOffset)
    End Sub

    Public Sub UpdatePlayhead(virtualTime As TimeSpan) Implements ITimelineController.UpdatePlayhead
        _renderer.UpdatePlayhead(virtualTime)
    End Sub

    Public Sub UpdateLoadingState(isLoading As Boolean, rotAngle As Single, fadeAlpha As Single) Implements ITimelineController.UpdateLoadingState
        _renderer.UpdateLoadingState(isLoading, rotAngle, fadeAlpha)
    End Sub

    Public Sub SetDataSources(caches As Object, extractors As Object) Implements ITimelineController.SetDataSources
        Dim typedCaches = TryCast(caches, Dictionary(Of String, Object))
        Dim typedExtractors = TryCast(extractors, Dictionary(Of String, Object))
        _renderer.SetDataSources(typedCaches, typedExtractors)
    End Sub

    Public Sub UpdateLayout(tileSize As Size, tileCount As Integer) Implements ITimelineController.UpdateLayout
        _renderer.UpdateLayout(tileSize, tileCount)
    End Sub

    Public Sub Resize(width As Integer, height As Integer) Implements ITimelineController.Resize
        _renderer.Resize(width, height)
    End Sub

    Public Sub ClearStrips() Implements ITimelineController.ClearStrips
        _renderer.ClearStrips()
    End Sub

    Public Sub SetAudioPeaks(peaks() As PeakMinMax, samplesPerPeak As Integer) Implements ITimelineController.SetAudioPeaks
        _renderer?.SetAudioPeaks(peaks, samplesPerPeak)
    End Sub

    Public Function LoadStripAsync(stripType As Integer, fallback As String) As Task Implements ITimelineController.LoadStripAsync
        Return _renderer.LoadStripAsync(stripType, fallback)
    End Function

    Private Sub OnPlayheadScrubbed(virtTime As TimeSpan)
        Dim currentTick As Long = Environment.TickCount64
        If (currentTick - Interlocked.Read(_lastScrubTick)) < SCRUB_INTERVAL_MS Then
            Return
        End If
        Interlocked.Exchange(_lastScrubTick, currentTick)

        RaiseEvent PlaybackPauseRequested()
        RaiseEvent SeekRequested(virtTime)
    End Sub

    Private Sub OnPlayheadSeekCompleted(virtTime As TimeSpan)
        RaiseEvent SeekRequested(virtTime)
    End Sub

    Private Sub OnMarkerStartChanged(newTime As TimeSpan)
        _model.SetMarkers(newTime, _model.MarkerEnd)
        InternalPushState()
        RaiseEvent MarkersChanged()
    End Sub

    Private Sub OnMarkerEndChanged(newTime As TimeSpan)
        _model.SetMarkers(_model.MarkerStart, newTime)
        InternalPushState()
        RaiseEvent MarkersChanged()
    End Sub

    Private Sub OnMarkersCommit()
        RaiseEvent MarkersCommitRequested()
    End Sub

    Private Sub OnAudioOffsetChanged(offset As TimeSpan)
        _lastAudioOffset = offset
        InternalPushState()
        RaiseEvent AudioOffsetChanged(offset)
    End Sub

    Private Sub OnAudioOffsetCommit(offset As TimeSpan)
        RaiseEvent AudioOffsetCommit(offset)
    End Sub

    Private Sub OnPreviewRequested(virtTime As TimeSpan)
        RaiseEvent PreviewRequested(virtTime)
    End Sub

    Private Sub OnPlaybackPauseRequested()
        RaiseEvent PlaybackPauseRequested()
    End Sub

    Private Sub OnCursorMoved(virtTime As TimeSpan, mouseX As Integer)
        _hoverVirtualTime = virtTime
        RaiseEvent CursorMoved()
    End Sub

    Private Sub OnCursorLeft()
        _hoverVirtualTime = Nothing
        RaiseEvent CursorMoved()
    End Sub
End Class