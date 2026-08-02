' Path: ITimelineController.vb
Option Strict On
Option Explicit On

Imports System
Imports System.Drawing
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports yoump.IServices

Public Interface ITimelineController
    Event SeekRequested As Action(Of TimeSpan)
    Event PreviewRequested As Action(Of TimeSpan)
    Event PlaybackPauseRequested As Action
    Event AudioOffsetChanged As Action(Of TimeSpan)
    Event AudioOffsetCommit As Action(Of TimeSpan)
    Event MarkersChanged As Action
    Event CursorMoved As Action
    Event MarkersCommitRequested As Action

    ReadOnly Property HoverVirtualTime As TimeSpan?

    Sub Initialize(pictureBox As PictureBox)
    Sub SetDataSources(caches As Object, extractors As Object)

    Sub PushState(fps As Double, hasSelection As Boolean, isAudioReplaced As Boolean, hasAudio As Boolean, audioOffset As TimeSpan, bakedAudioOffset As TimeSpan)
    Sub UpdatePlayhead(virtualTime As TimeSpan)
    Sub UpdateLoadingState(isLoading As Boolean, rotAngle As Single, fadeAlpha As Single)

    Sub UpdateLayout(tileSize As Size, tileCount As Integer)
    Sub Resize(width As Integer, height As Integer)

    Sub ClearStrips()
    Function LoadStripAsync(stripType As Integer, fallback As String) As Task

    Sub SetAudioPeaks(peaks() As PeakMinMax, samplesPerPeak As Integer)
End Interface