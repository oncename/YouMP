Imports System
Imports System.Collections.Concurrent
Imports System.Threading

Public Class GpuFrameCacheManager
    Implements IDisposable

    ' Внутренний класс для хранения кадра и метки времени его последнего использования
    Private Class CacheItem
        Public Frame As GpuVideoFrame
        Public LastAccessTicks As Long
    End Class

    Private ReadOnly _pool As GpuFramePool
    Private ReadOnly _cache As New ConcurrentDictionary(Of Integer, CacheItem)()
    Private ReadOnly _writeLock As New Object()
    Private ReadOnly _maxFrames As Integer

    Private _disposedValue As Boolean

    Public ReadOnly SlotWidth As Integer
    Public ReadOnly SlotHeight As Integer
    Public ReadOnly SlotInterval As TimeSpan
    Public ReadOnly TotalSlots As Integer

    Public Event FrameCached As Action(Of Integer)

    Public Sub New(pool As GpuFramePool, intervalSec As Double, duration As TimeSpan, maxFrames As Integer)
        _pool = pool
        _maxFrames = maxFrames
        SlotWidth = pool.Width
        SlotHeight = pool.Height
        SlotInterval = TimeSpan.FromSeconds(intervalSec)

        If duration > TimeSpan.Zero AndAlso intervalSec > 0 Then
            TotalSlots = CInt(Math.Ceiling(duration.TotalSeconds / intervalSec))
        Else
            TotalSlots = 0
        End If
    End Sub

    Public ReadOnly Property Pool As GpuFramePool
        Get
            Return _pool
        End Get
    End Property

    Public Function GetTimeForSlot(index As Integer) As TimeSpan
        Return TimeSpan.FromSeconds(index * SlotInterval.TotalSeconds)
    End Function

    Public Function IsFrameCached(index As Integer) As Boolean
        If _disposedValue Then Return False

        ' Lock-free проверка
        Return _cache.ContainsKey(index)
    End Function

    Public Function GetFrame(index As Integer) As GpuVideoFrame
        If _disposedValue Then Return Nothing

        Dim item As CacheItem = Nothing
        ' Lock-free чтение: не блокирует другие потоки, запрашивающие кадры
        If _cache.TryGetValue(index, item) Then
            ' Атомарное обновление времени доступа для логики LRU
            Interlocked.Exchange(item.LastAccessTicks, Environment.TickCount64)
            Return item.Frame
        End If

        Return Nothing
    End Function

    Public Sub CommitFrame(index As Integer, frame As GpuVideoFrame)
        If _disposedValue Then
            ' Если пытаемся положить кадр в уже уничтоженный кэш - сразу возвращаем его в пул
            frame?.Dispose()
            Return
        End If

        Dim newItem As New CacheItem With {
            .Frame = frame,
            .LastAccessTicks = Environment.TickCount64
        }

        ' Блокировка применяется только при записи нового кадра
        SyncLock _writeLock
            If _cache.Count >= _maxFrames AndAlso Not _cache.ContainsKey(index) Then
                ' Вытеснение (LRU): находим кадр с наименьшим LastAccessTicks
                Dim oldestKey As Integer = -1
                Dim oldestTicks As Long = Long.MaxValue

                For Each kvp In _cache
                    Dim ticks As Long = Interlocked.Read(kvp.Value.LastAccessTicks)
                    If ticks < oldestTicks Then
                        oldestTicks = ticks
                        oldestKey = kvp.Key
                    End If
                Next

                If oldestKey <> -1 Then
                    Dim removedItem As CacheItem = Nothing
                    If _cache.TryRemove(oldestKey, removedItem) Then
                        removedItem.Frame.Dispose() ' Автоматически возвращается в пул
                    End If
                End If
            End If

            _cache(index) = newItem
        End SyncLock

        RaiseEvent FrameCached(index)
    End Sub

    Protected Overridable Sub Dispose(disposing As Boolean)
        If Not _disposedValue Then
            If disposing Then
                SyncLock _writeLock
                    For Each kvp In _cache
                        kvp.Value.Frame?.Dispose()
                    Next
                    _cache.Clear()
                End SyncLock
            End If
            _disposedValue = True
        End If
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        Dispose(disposing:=True)
        GC.SuppressFinalize(Me)
    End Sub
End Class