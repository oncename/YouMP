Option Strict On
Option Explicit On

Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading
Imports yoump.IServices

' =========================================================================
' ПАТТЕРН COMMAND (СИСТЕМА UNDO / REDO)
' =========================================================================

Public Interface IUndoableCommand
    Sub Execute()
    Sub Undo()
End Interface

Public Class DelegateCommand
    Implements IUndoableCommand
    Private ReadOnly _execute As Action
    Private ReadOnly _undo As Action

    Public Sub New(execute As Action, undo As Action)
        _execute = execute
        _undo = undo
    End Sub

    Public Sub Execute() Implements IUndoableCommand.Execute
        _execute()
    End Sub

    Public Sub Undo() Implements IUndoableCommand.Undo
        _undo()
    End Sub
End Class

Public Class UndoManager
    Private ReadOnly _undoStack As New Stack(Of IUndoableCommand)()
    Private ReadOnly _redoStack As New Stack(Of IUndoableCommand)()

    Public Sub ExecuteAndPush(cmd As IUndoableCommand)
        cmd.Execute()
        _undoStack.Push(cmd)
        _redoStack.Clear()
    End Sub

    Public Sub PushJustRecord(cmd As IUndoableCommand)
        _undoStack.Push(cmd)
        _redoStack.Clear()
    End Sub

    Public Sub Undo()
        If _undoStack.Count > 0 Then
            Dim cmd = _undoStack.Pop()
            cmd.Undo()
            _redoStack.Push(cmd)
        End If
    End Sub

    Public Sub Redo()
        If _redoStack.Count > 0 Then
            Dim cmd = _redoStack.Pop()
            cmd.Execute()
            _undoStack.Push(cmd)
        End If
    End Sub
End Class

' =========================================================================
' СТРУКТУРЫ ДЛЯ МНОГОДОРОЖЕЧНОЙ АРХИТЕКТУРЫ
' =========================================================================

''' <summary>
''' Описывает отдельный медиаклип, лежащий на таймлайне.
''' </summary>
Public Class MediaClip
    Public Property Id As Guid = Guid.NewGuid()
    Public Property FilePath As String
    Public Property MediaType As TargetFormatType

    ' Свойства исходного файла
    Public Property SourceDuration As TimeSpan
    Public Property SourceIn As TimeSpan ' Обрезка слева (Trim In)
    Public Property SourceOut As TimeSpan ' Обрезка справа (Trim Out)

    ' Положение на таймлайне (на холсте)
    Public Property TimelineStart As TimeSpan
    Public ReadOnly Property TimelineEnd As TimeSpan
        Get
            Return TimelineStart + (SourceOut - SourceIn)
        End Get
    End Property

    ' Настройки конкретного клипа
    Public Property Volume As Single = 1.0F
    Public Property FadeIn As TimeSpan = TimeSpan.Zero
    Public Property FadeOut As TimeSpan = TimeSpan.Zero

    ' ====================================================
    ' НОВЫЕ СВОЙСТВА ДЛЯ ТРАНСФОРМАЦИИ (Picture-in-Picture)
    ' ====================================================
    Public Property Scale As Single = 1.0F
    Public Property PositionX As Single = 0.0F
    Public Property PositionY As Single = 0.0F
    Public Property Rotation As Single = 0.0F

    ''' <summary>
    ''' Переводит глобальное время таймлайна в физическое время этого исходного файла.
    ''' </summary>
    Public Function GetPhysicalTime(timelineTime As TimeSpan) As TimeSpan?
        If timelineTime >= TimelineStart AndAlso timelineTime < TimelineEnd Then
            Return SourceIn + (timelineTime - TimelineStart)
        End If
        Return Nothing
    End Function

    Public Function Clone() As MediaClip
        Return CType(Me.MemberwiseClone(), MediaClip)
    End Function

    Public Sub ApplyState(other As MediaClip)
        Me.FilePath = other.FilePath
        Me.MediaType = other.MediaType
        Me.SourceDuration = other.SourceDuration
        Me.SourceIn = other.SourceIn
        Me.SourceOut = other.SourceOut
        Me.TimelineStart = other.TimelineStart
        Me.Volume = other.Volume
        Me.FadeIn = other.FadeIn
        Me.FadeOut = other.FadeOut

        ' Применяем новые свойства
        Me.Scale = other.Scale
        Me.PositionX = other.PositionX
        Me.PositionY = other.PositionY
        Me.Rotation = other.Rotation
    End Sub
End Class

''' <summary>
''' Описывает дорожку (слой) таймлайна.
''' </summary>
Public Class TrackModel
    Public Property Id As Guid = Guid.NewGuid()
    Public Property Name As String
    Public Property Type As TargetFormatType
    Public Property IsMuted As Boolean = False
    Public Property IsSolo As Boolean = False

    Public ReadOnly Clips As New List(Of MediaClip)()

    Public Function GetClipAtTime(timelineTime As TimeSpan) As MediaClip
        For Each clip In Clips
            If timelineTime >= clip.TimelineStart AndAlso timelineTime < clip.TimelineEnd Then
                Return clip
            End If
        Next
        Return Nothing
    End Function
End Class

' =========================================================================
' ОСНОВНАЯ МОДЕЛЬ ПРОЕКТА
' =========================================================================

Public Class ProjectModel
    Implements IDisposable

    Private ReadOnly _tracks As New List(Of TrackModel)()

    Private _markerStart As TimeSpan = TimeSpan.Zero
    Private _markerEnd As TimeSpan = TimeSpan.Zero

    Private _isZoomed As Boolean = False
    Private _viewStart As TimeSpan = TimeSpan.Zero
    Private _viewEnd As TimeSpan = TimeSpan.Zero

    Private ReadOnly _zoomHistory As New CircularStack(Of Tuple(Of TimeSpan, TimeSpan))(50)
    Private ReadOnly _stateLock As New ReaderWriterLockSlim()
    Private _disposed As Boolean = False

    Public Event StateChanged As Action

    Public ReadOnly Property History As New UndoManager()

    Public Sub New()
        ' Инициализируем классическую структуру слоев NLE (по умолчанию 1 видео и 1 аудио)
        _tracks.Add(New TrackModel() With {.Name = "Video 1", .Type = TargetFormatType.Video})
        _tracks.Add(New TrackModel() With {.Name = "Audio 1", .Type = TargetFormatType.Audio})
    End Sub

    Public Sub Undo()
        History.Undo()
    End Sub

    Public Sub Redo()
        History.Redo()
    End Sub

    Public ReadOnly Property Tracks As IReadOnlyList(Of TrackModel)
        Get
            _stateLock.EnterReadLock()
            Try
                Return _tracks.ToList().AsReadOnly()
            Finally
                _stateLock.ExitReadLock()
            End Try
        End Get
    End Property

    Private Function GetTotalDurationInternal() As TimeSpan
        Dim maxDur As TimeSpan = TimeSpan.Zero
        For Each track In _tracks
            For Each clip In track.Clips
                If clip.TimelineEnd > maxDur Then
                    maxDur = clip.TimelineEnd
                End If
            Next
        Next
        Return maxDur
    End Function

    Public ReadOnly Property TotalDuration As TimeSpan
        Get
            _stateLock.EnterReadLock()
            Try
                Return GetTotalDurationInternal()
            Finally
                _stateLock.ExitReadLock()
            End Try
        End Get
    End Property

    Public ReadOnly Property AudioDuration As TimeSpan
        Get
            Return TotalDuration
        End Get
    End Property

    ' Вспомогательный класс для передачи контекста воспроизведения
    Public Class PlayheadContext
        Public Clip As MediaClip
        Public PhysicalTime As TimeSpan
    End Class

    ' Функция, которая определяет, какой видеоклип находится под плейхедом, и вычисляет его реальное время
    Public Function GetVideoContextAtTime(virtTime As TimeSpan) As PlayheadContext
        _stateLock.EnterReadLock()
        Try
            ' Перебираем треки с видео. В NLE слои рисуются снизу вверх, 
            ' поэтому верхний индекс перекрывает нижние. Ищем с конца списка.
            For i As Integer = _tracks.Count - 1 To 0 Step -1
                Dim t = _tracks(i)
                If t.Type = TargetFormatType.Video AndAlso Not t.IsMuted Then
                    Dim clip = t.GetClipAtTime(virtTime)
                    If clip IsNot Nothing Then
                        ' Вычисляем физическое время файла с учетом подрезки (SourceIn)
                        Dim localTime = virtTime - clip.TimelineStart
                        Dim physTime = clip.SourceIn + localTime

                        Return New PlayheadContext With {
                            .Clip = clip,
                            .PhysicalTime = physTime
                        }
                    End If
                End If
            Next
            Return Nothing
        Finally
            _stateLock.ExitReadLock()
        End Try
    End Function

    ' =========================================================================
    ' УПРАВЛЕНИЕ КЛИПАМИ И ТРЕКАМИ
    ' =========================================================================

    Public Sub AddTrack(type As TargetFormatType, name As String)
        _stateLock.EnterWriteLock()
        Try
            Dim newTrack As New TrackModel() With {
                .Id = Guid.NewGuid(),
                .Name = name,
                .Type = type
            }
            ' Видеотреки добавляем наверх (в начало списка), Аудио - вниз
            If type = TargetFormatType.Video Then
                _tracks.Insert(0, newTrack)
            Else
                _tracks.Add(newTrack)
            End If
        Finally
            _stateLock.ExitWriteLock()
        End Try
        RaiseEvent StateChanged()
    End Sub

    Public Sub RemoveLastTrack()
        _stateLock.EnterWriteLock()
        Try
            If _tracks.Count > 1 Then
                ' Ищем пустой трек с конца
                Dim trackToRemove = _tracks.LastOrDefault(Function(t) t.Clips.Count = 0)
                ' Если пустых нет, удаляем просто самый последний
                If trackToRemove Is Nothing Then trackToRemove = _tracks.Last()

                _tracks.Remove(trackToRemove)
            End If
        Finally
            _stateLock.ExitWriteLock()
        End Try
        RaiseEvent StateChanged()
    End Sub

    Public Sub AddClipToTrack(trackIndex As Integer, clip As MediaClip)
        Dim changed As Boolean = False
        _stateLock.EnterWriteLock()
        Try
            If trackIndex >= 0 AndAlso trackIndex < _tracks.Count Then
                _tracks(trackIndex).Clips.Add(clip)
                _tracks(trackIndex).Clips.Sort(Function(c1, c2) c1.TimelineStart.CompareTo(c2.TimelineStart))
                changed = True
            End If
        Finally
            _stateLock.ExitWriteLock()
        End Try
        If changed Then RaiseEvent StateChanged()
    End Sub

    Public Sub AddClipSequential(clip As MediaClip)
        Dim changed As Boolean = False
        _stateLock.EnterWriteLock()
        Try
            Dim targetTrack As TrackModel = Nothing

            ' 1. Ищем базовый трек по имени (чтобы файлы падали на основные слои, а не перекрывающие)
            If clip.MediaType = TargetFormatType.Video Then
                targetTrack = _tracks.FirstOrDefault(Function(t) t.Name = "Video 1")
            ElseIf clip.MediaType = TargetFormatType.Audio Then
                targetTrack = _tracks.FirstOrDefault(Function(t) t.Name = "Audio 1")
            End If

            ' 2. Если базовый трек удален, берем первый попавшийся подходящего типа
            If targetTrack Is Nothing Then
                targetTrack = _tracks.FirstOrDefault(Function(t) t.Type = clip.MediaType)
            End If

            ' 3. Если вообще нет трека нужного типа - создаем новый динамически
            If targetTrack Is Nothing Then
                targetTrack = New TrackModel() With {
                    .Id = Guid.NewGuid(),
                    .Name = If(clip.MediaType = TargetFormatType.Video, "Video", "Audio") & " " & (_tracks.Count + 1),
                    .Type = clip.MediaType
                }
                _tracks.Add(targetTrack)
            End If

            ' 4. Вычисляем время окончания самого последнего клипа на этом треке
            Dim insertTime As TimeSpan = TimeSpan.Zero
            If targetTrack.Clips.Count > 0 Then
                insertTime = targetTrack.Clips.Max(Function(c) c.TimelineEnd)
            End If

            ' 5. Пристыковываем новый клип
            clip.TimelineStart = insertTime
            targetTrack.Clips.Add(clip)

            ' Обязательная сортировка хронологии
            targetTrack.Clips.Sort(Function(c1, c2) c1.TimelineStart.CompareTo(c2.TimelineStart))
            changed = True
        Finally
            _stateLock.ExitWriteLock()
        End Try

        If changed Then RaiseEvent StateChanged()
    End Sub

    Public Sub ClearAllClips()
        Dim changed As Boolean = False
        _stateLock.EnterWriteLock()
        Try
            For Each track In _tracks
                If track.Clips.Count > 0 Then
                    track.Clips.Clear()
                    changed = True
                End If
            Next
        Finally
            _stateLock.ExitWriteLock()
        End Try
        If changed Then RaiseEvent StateChanged()
    End Sub

    Public Sub SplitClipAtTime(splitTime As TimeSpan)
        Dim actions As New List(Of Action)()
        Dim undoActions As New List(Of Action)()
        Dim tracksModified As Boolean = False

        _stateLock.EnterWriteLock()
        Try
            For i As Integer = 0 To _tracks.Count - 1
                Dim track = _tracks(i)

                Dim clipsToSplit = track.Clips.Where(Function(c) c.TimelineStart < splitTime.Subtract(TimeSpan.FromMilliseconds(1)) AndAlso
                                                             c.TimelineEnd > splitTime.Add(TimeSpan.FromMilliseconds(1))).ToList()

                For Each originalClip In clipsToSplit
                    Dim offset As TimeSpan = splitTime - originalClip.TimelineStart

                    Dim rightClip As New MediaClip With {
                        .Id = Guid.NewGuid(),
                        .FilePath = originalClip.FilePath,
                        .MediaType = originalClip.MediaType,
                        .SourceDuration = originalClip.SourceDuration,
                        .Volume = originalClip.Volume,
                        .FadeOut = originalClip.FadeOut,
                        .FadeIn = TimeSpan.Zero,
                        .SourceIn = originalClip.SourceIn + offset,
                        .SourceOut = originalClip.SourceOut,
                        .TimelineStart = splitTime,
                        .Scale = originalClip.Scale,
                        .PositionX = originalClip.PositionX,
                        .PositionY = originalClip.PositionY,
                        .Rotation = originalClip.Rotation
                    }

                    Dim oldSourceOut = originalClip.SourceOut
                    Dim oldFadeOut = originalClip.FadeOut
                    Dim targetClip = originalClip

                    actions.Add(Sub()
                                    targetClip.SourceOut = targetClip.SourceIn + offset
                                    targetClip.FadeOut = TimeSpan.Zero
                                    track.Clips.Add(rightClip)
                                    track.Clips.Sort(Function(c1, c2) c1.TimelineStart.CompareTo(c2.TimelineStart))
                                End Sub)

                    undoActions.Add(Sub()
                                        targetClip.SourceOut = oldSourceOut
                                        targetClip.FadeOut = oldFadeOut
                                        track.Clips.Remove(rightClip)
                                    End Sub)
                    tracksModified = True
                Next
            Next
        Finally
            _stateLock.ExitWriteLock()
        End Try

        If tracksModified Then
            Dim cmd As New DelegateCommand(
                Sub()
                    _stateLock.EnterWriteLock()
                    Try
                        For Each act In actions : act() : Next
                    Finally
                        _stateLock.ExitWriteLock()
                    End Try
                    RaiseEvent StateChanged()
                End Sub,
                Sub()
                    _stateLock.EnterWriteLock()
                    Try
                        For Each undoAct In undoActions : undoAct() : Next
                    Finally
                        _stateLock.ExitWriteLock()
                    End Try
                    RaiseEvent StateChanged()
                End Sub
            )
            History.ExecuteAndPush(cmd)
        End If
    End Sub

    ' Перемещает клип на указанный трек и заданное время (если нет коллизий)
    Public Function MoveClip(clipId As Guid, targetTrackId As Guid, newStartTime As TimeSpan) As Boolean
        Dim changed As Boolean = False
        _stateLock.EnterWriteLock()
        Try
            ' 1. Ищем клип и его текущий трек
            Dim sourceTrack As TrackModel = Nothing
            Dim targetClip As MediaClip = Nothing

            For Each t In _tracks
                targetClip = t.Clips.FirstOrDefault(Function(c) c.Id = clipId)
                If targetClip IsNot Nothing Then
                    sourceTrack = t
                    Exit For
                End If
            Next

            If targetClip Is Nothing OrElse sourceTrack Is Nothing Then Return False

            ' 2. Ищем целевой трек
            Dim destTrack = _tracks.FirstOrDefault(Function(t) t.Id = targetTrackId)
            If destTrack Is Nothing Then Return False

            ' 3. Защита от межвидового скрещивания (Видео на Видео, Аудио на Аудио)
            If destTrack.Type <> targetClip.MediaType Then Return False

            ' 4. Проверка коллизий на целевом треке
            Dim clipDuration = targetClip.TimelineEnd - targetClip.TimelineStart
            Dim newEndTime = newStartTime + clipDuration

            For Each c In destTrack.Clips
                If c.Id = targetClip.Id Then Continue For
                ' Если отрезки времени пересекаются - отменяем перенос (Коллизия)
                If newStartTime < c.TimelineEnd AndAlso newEndTime > c.TimelineStart Then
                    Return False
                End If
            Next

            ' 5. Совершаем перенос
            If sourceTrack.Id <> destTrack.Id Then
                sourceTrack.Clips.Remove(targetClip)
                destTrack.Clips.Add(targetClip)
            End If

            targetClip.TimelineStart = newStartTime

            ' Сортируем клипы на целевом треке по времени (чтобы рендер не сходил с ума)
            destTrack.Clips.Sort(Function(c1, c2) c1.TimelineStart.CompareTo(c2.TimelineStart))
            changed = True
        Finally
            _stateLock.ExitWriteLock()
        End Try

        If changed Then RaiseEvent StateChanged()
        Return changed
    End Function

    Public Sub RemoveClip(clipId As Guid)
        Dim trackIdx As Integer = -1
        Dim clipToRemove As MediaClip = Nothing

        _stateLock.EnterReadLock()
        Try
            For i As Integer = 0 To _tracks.Count - 1
                clipToRemove = _tracks(i).Clips.FirstOrDefault(Function(c) c.Id = clipId)
                If clipToRemove IsNot Nothing Then
                    trackIdx = i
                    Exit For
                End If
            Next
        Finally
            _stateLock.ExitReadLock()
        End Try

        If clipToRemove IsNot Nothing Then
            Dim cmd As New DelegateCommand(
                Sub()
                    _stateLock.EnterWriteLock()
                    Try
                        _tracks(trackIdx).Clips.Remove(clipToRemove)
                    Finally
                        _stateLock.ExitWriteLock()
                    End Try
                    RaiseEvent StateChanged()
                End Sub,
                Sub()
                    _stateLock.EnterWriteLock()
                    Try
                        _tracks(trackIdx).Clips.Add(clipToRemove)
                        _tracks(trackIdx).Clips.Sort(Function(c1, c2) c1.TimelineStart.CompareTo(c2.TimelineStart))
                    Finally
                        _stateLock.ExitWriteLock()
                    End Try
                    RaiseEvent StateChanged()
                End Sub
            )
            History.ExecuteAndPush(cmd)
        End If
    End Sub

    Public Sub UpdateClipStateWithHistory(clipId As Guid, newState As MediaClip, newTrackIdx As Integer, oldState As MediaClip, oldTrackIdx As Integer)
        Dim cmd As New DelegateCommand(
            Sub()
                _stateLock.EnterWriteLock()
                Try
                    If oldTrackIdx <> newTrackIdx Then
                        Dim c = _tracks(oldTrackIdx).Clips.FirstOrDefault(Function(x) x.Id = clipId)
                        If c IsNot Nothing Then
                            _tracks(oldTrackIdx).Clips.Remove(c)
                            _tracks(newTrackIdx).Clips.Add(c)
                        End If
                    End If
                    Dim clipToUpdate = _tracks(newTrackIdx).Clips.FirstOrDefault(Function(x) x.Id = clipId)
                    If clipToUpdate IsNot Nothing Then
                        clipToUpdate.ApplyState(newState)
                        _tracks(newTrackIdx).Clips.Sort(Function(c1, c2) c1.TimelineStart.CompareTo(c2.TimelineStart))
                    End If
                Finally
                    _stateLock.ExitWriteLock()
                End Try
                RaiseEvent StateChanged()
            End Sub,
            Sub()
                _stateLock.EnterWriteLock()
                Try
                    If oldTrackIdx <> newTrackIdx Then
                        Dim c = _tracks(newTrackIdx).Clips.FirstOrDefault(Function(x) x.Id = clipId)
                        If c IsNot Nothing Then
                            _tracks(newTrackIdx).Clips.Remove(c)
                            _tracks(oldTrackIdx).Clips.Add(c)
                        End If
                    End If
                    Dim clipToUpdate = _tracks(oldTrackIdx).Clips.FirstOrDefault(Function(x) x.Id = clipId)
                    If clipToUpdate IsNot Nothing Then
                        clipToUpdate.ApplyState(oldState)
                        _tracks(oldTrackIdx).Clips.Sort(Function(c1, c2) c1.TimelineStart.CompareTo(c2.TimelineStart))
                    End If
                Finally
                    _stateLock.ExitWriteLock()
                End Try
                RaiseEvent StateChanged()
            End Sub
        )
        History.PushJustRecord(cmd)
    End Sub

    ' =========================================================================
    ' МАРКЕРЫ, ЗУМ И ПАНОРАМИРОВАНИЕ (Адаптировано)
    ' =========================================================================

    Public Property MarkerStart As TimeSpan
        Get
            _stateLock.EnterReadLock()
            Try
                Return _markerStart
            Finally
                _stateLock.ExitReadLock()
            End Try
        End Get
        Set(value As TimeSpan)
            Dim changed As Boolean = False
            _stateLock.EnterWriteLock()
            Try
                If _markerStart <> value Then
                    _markerStart = value
                    changed = True
                End If
            Finally
                _stateLock.ExitWriteLock()
            End Try
            If changed Then RaiseEvent StateChanged()
        End Set
    End Property

    Public Property MarkerEnd As TimeSpan
        Get
            _stateLock.EnterReadLock()
            Try
                Return _markerEnd
            Finally
                _stateLock.ExitReadLock()
            End Try
        End Get
        Set(value As TimeSpan)
            Dim changed As Boolean = False
            _stateLock.EnterWriteLock()
            Try
                If _markerEnd <> value Then
                    _markerEnd = value
                    changed = True
                End If
            Finally
                _stateLock.ExitWriteLock()
            End Try
            If changed Then RaiseEvent StateChanged()
        End Set
    End Property

    Public Property IsZoomed As Boolean
        Get
            _stateLock.EnterReadLock()
            Try
                Return _isZoomed
            Finally
                _stateLock.ExitReadLock()
            End Try
        End Get
        Set(value As Boolean)
            Dim changed As Boolean = False
            _stateLock.EnterWriteLock()
            Try
                If _isZoomed <> value Then
                    _isZoomed = value
                    changed = True
                End If
            Finally
                _stateLock.ExitWriteLock()
            End Try
            If changed Then RaiseEvent StateChanged()
        End Set
    End Property

    Public Property ViewStart As TimeSpan
        Get
            _stateLock.EnterReadLock()
            Try
                Return _viewStart
            Finally
                _stateLock.ExitReadLock()
            End Try
        End Get
        Set(value As TimeSpan)
            Dim changed As Boolean = False
            _stateLock.EnterWriteLock()
            Try
                If _viewStart <> value Then
                    _viewStart = If(value < TimeSpan.Zero, TimeSpan.Zero, value)
                    changed = True
                End If
            Finally
                _stateLock.ExitWriteLock()
            End Try
            If changed Then RaiseEvent StateChanged()
        End Set
    End Property

    Public Property ViewEnd As TimeSpan
        Get
            _stateLock.EnterReadLock()
            Try
                Return _viewEnd
            Finally
                _stateLock.ExitReadLock()
            End Try
        End Get
        Set(value As TimeSpan)
            Dim changed As Boolean = False
            _stateLock.EnterWriteLock()
            Try
                Dim currentTotal = GetTotalDurationInternal()
                If value > currentTotal Then value = currentTotal
                If _viewEnd <> value Then
                    _viewEnd = value
                    changed = True
                End If
            Finally
                _stateLock.ExitWriteLock()
            End Try
            If changed Then RaiseEvent StateChanged()
        End Set
    End Property

    Public Sub SetMarkers(mStart As TimeSpan, mEnd As TimeSpan)
        Dim changed As Boolean = False
        _stateLock.EnterWriteLock()
        Try
            Dim currentTotal = GetTotalDurationInternal()
            If currentTotal > TimeSpan.Zero Then
                If mEnd > currentTotal Then mEnd = currentTotal
                If mStart < TimeSpan.Zero Then mStart = TimeSpan.Zero
                If mStart > mEnd Then
                    Dim temp As TimeSpan = mStart
                    mStart = mEnd
                    mEnd = temp
                End If
            End If

            If _markerStart <> mStart OrElse _markerEnd <> mEnd Then
                _markerStart = mStart
                _markerEnd = mEnd
                changed = True
            End If
        Finally
            _stateLock.ExitWriteLock()
        End Try
        If changed Then RaiseEvent StateChanged()
    End Sub

    Public Sub ZoomIn(newViewStart As TimeSpan, newViewEnd As TimeSpan)
        If newViewStart >= newViewEnd Then Return
        Dim changed As Boolean = False
        _stateLock.EnterWriteLock()
        Try
            If _viewStart <> newViewStart OrElse _viewEnd <> newViewEnd Then
                _zoomHistory.Push(Tuple.Create(_viewStart, _viewEnd))
                _isZoomed = True
                _viewStart = If(newViewStart < TimeSpan.Zero, TimeSpan.Zero, newViewStart)

                Dim currentTotal = GetTotalDurationInternal()
                _viewEnd = If(newViewEnd > currentTotal, currentTotal, newViewEnd)
                changed = True
            End If
        Finally
            _stateLock.ExitWriteLock()
        End Try
        If changed Then RaiseEvent StateChanged()
    End Sub

    Public Sub ZoomOut()
        Dim changed As Boolean = False
        _stateLock.EnterWriteLock()
        Try
            If Not _isZoomed Then Return
            Dim currentTotal = GetTotalDurationInternal()

            If _zoomHistory.Count > 0 Then
                Dim prevView = _zoomHistory.Pop()
                _viewStart = prevView.Item1
                _viewEnd = prevView.Item2
                If _viewEnd > currentTotal Then _viewEnd = currentTotal
                If _viewStart > _viewEnd Then _viewStart = TimeSpan.Zero
                _isZoomed = Not (_zoomHistory.Count = 0 AndAlso _viewStart = TimeSpan.Zero AndAlso _viewEnd = currentTotal)
            Else
                _isZoomed = False
                _viewStart = TimeSpan.Zero
                _viewEnd = currentTotal
            End If
            changed = True
        Finally
            _stateLock.ExitWriteLock()
        End Try
        If changed Then RaiseEvent StateChanged()
    End Sub

    Public Sub Pan(offset As TimeSpan)
        If offset = TimeSpan.Zero Then Return
        Dim changed As Boolean = False
        _stateLock.EnterWriteLock()
        Try
            Dim currentDuration = _viewEnd - _viewStart
            Dim newStart = _viewStart + offset
            Dim newEnd = _viewEnd + offset
            Dim currentTotal = GetTotalDurationInternal()

            If newStart < TimeSpan.Zero Then
                newStart = TimeSpan.Zero
                newEnd = newStart + currentDuration
            ElseIf newEnd > currentTotal Then
                newEnd = currentTotal
                newStart = newEnd - currentDuration
                If newStart < TimeSpan.Zero Then newStart = TimeSpan.Zero
            End If

            If _viewStart <> newStart OrElse _viewEnd <> newEnd Then
                _viewStart = newStart
                _viewEnd = newEnd
                changed = True
            End If
        Finally
            _stateLock.ExitWriteLock()
        End Try
        If changed Then RaiseEvent StateChanged()
    End Sub

    Public Sub ResetZoomHistory()
        Dim changed As Boolean = False
        _stateLock.EnterWriteLock()
        Try
            Dim currentTotal = GetTotalDurationInternal()
            _zoomHistory.Clear()
            If _isZoomed OrElse _viewStart <> TimeSpan.Zero OrElse _viewEnd <> currentTotal Then
                _isZoomed = False
                _viewStart = TimeSpan.Zero
                _viewEnd = currentTotal
                changed = True
            End If
        Finally
            _stateLock.ExitWriteLock()
        End Try
        If changed Then RaiseEvent StateChanged()
    End Sub

    Public Function GetTimelineStateData() As TimelineStateData
        _stateLock.EnterReadLock()
        Try
            Dim dur = GetTotalDurationInternal()

            ' Создаем глубокую копию (Snapshot) всех треков и клипов
            Dim trackSnapshots As New List(Of TrackSnapshot)()
            For Each t In _tracks
                Dim clonedClips = t.Clips.Select(Function(c) c.Clone()).ToList().AsReadOnly()
                trackSnapshots.Add(New TrackSnapshot With {
                    .Id = t.Id,
                    .Name = t.Name,
                    .Type = t.Type,
                    .IsMuted = t.IsMuted,
                    .Clips = clonedClips
                })
            Next

            Return New TimelineStateData() With {
                .Duration = dur,
                .AudioDuration = dur,
                .MarkerStart = _markerStart,
                .MarkerEnd = _markerEnd,
                .IsZoomed = _isZoomed,
                .ViewStart = _viewStart,
                .ViewEnd = _viewEnd,
                .CutRegions = New List(Of CutRegionData)(),
                .Tracks = trackSnapshots.AsReadOnly() ' Передаем слепок
            }
        Finally
            _stateLock.ExitReadLock()
        End Try
    End Function

    ' =========================================================================
    ' НАСЛЕДИЕ (LEGACY BRIDGE)
    ' =========================================================================

    Public ReadOnly Property VirtualDuration As TimeSpan
        Get
            If _disposed Then Return TimeSpan.Zero
            Return TotalDuration
        End Get
    End Property

    Public Sub ClearCuts()
        If _disposed Then Return
    End Sub

    Public Sub AddCutRegion(startTs As TimeSpan, endTs As TimeSpan)
        If _disposed OrElse startTs >= endTs Then Return
    End Sub

    Public ReadOnly Property Cuts As IReadOnlyList(Of CutRegionData)
        Get
            If _disposed Then Return New List(Of CutRegionData)().AsReadOnly()
            Return New List(Of CutRegionData)().AsReadOnly()
        End Get
    End Property

    Public Function VirtualToPhysicalTime(virtTime As TimeSpan) As TimeSpan
        If _disposed Then Return virtTime
        Return virtTime
    End Function

    Public Function PhysicalToVirtualTime(physTime As TimeSpan) As TimeSpan
        If _disposed Then Return physTime
        Return physTime
    End Function

    Public Function GetSkipTargetIfInCut(physTime As TimeSpan) As TimeSpan?
        If _disposed OrElse physTime < TimeSpan.Zero Then Return Nothing
        Return Nothing
    End Function

    Public Function GetPlayableSegments() As IReadOnlyList(Of Tuple(Of TimeSpan, TimeSpan))
        If _disposed Then Return Nothing
        Return New List(Of Tuple(Of TimeSpan, TimeSpan)) From {
            Tuple.Create(TimeSpan.Zero, TotalDuration)
        }.AsReadOnly()
    End Function

    Protected Overridable Sub Dispose(disposing As Boolean)
        If Not _disposed Then
            If disposing Then
                _stateLock.Dispose()
            End If
            _disposed = True
        End If
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        Dispose(True)
        GC.SuppressFinalize(Me)
    End Sub
End Class