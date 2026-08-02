Option Strict On
Option Explicit On

Imports System.Collections.Generic
Imports System.Threading
Imports yoump.IServices

Public Class PreviewCacheManager
    Implements IDisposable

    Protected Friend Class CacheItem
        Public Property Key As String
        Public Property PooledData As PooledFrameBuffer
        Public Property Width As Integer
        Public Property Height As Integer
        Public Property RefCount As Integer = 0
        Public Property IsEvicted As Boolean = False
    End Class

    Public Class CachedFrameHandle
        Implements IDisposable

        Public ReadOnly Property Buffer As PooledFrameBuffer
        Private ReadOnly _manager As PreviewCacheManager
        Private ReadOnly _item As CacheItem
        Private _disposed As Boolean = False

        Friend Sub New(manager As PreviewCacheManager, item As CacheItem)
            _manager = manager
            _item = item
            Buffer = item.PooledData
        End Sub

        Protected Overridable Sub Dispose(disposing As Boolean)
            If Not _disposed Then
                If disposing Then
                    _manager.ReleaseItem(_item)
                End If
                _disposed = True
            End If
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            Dispose(disposing:=True)
            GC.SuppressFinalize(Me)
        End Sub
    End Class

    Private ReadOnly _cacheLock As New Object()
    Private ReadOnly _cacheDict As New Dictionary(Of String, LinkedListNode(Of CacheItem))()
    Private ReadOnly _cacheList As New LinkedList(Of CacheItem)()
    Private ReadOnly _maxSize As Integer

    Public Sub New(Optional maxSize As Integer = 30)
        _maxSize = maxSize
    End Sub

    Public Function TryGet(key As String, targetW As Integer, targetH As Integer) As CachedFrameHandle
        SyncLock _cacheLock
            Dim node As LinkedListNode(Of CacheItem) = Nothing
            If _cacheDict.TryGetValue(key, node) Then
                Dim item = node.Value

                If item.Width = targetW AndAlso item.Height = targetH Then
                    ' Размеры совпадают. Обновляем LRU-статус и выдаем хэндл.
                    _cacheList.Remove(node)
                    _cacheList.AddLast(node)

                    item.RefCount += 1
                    Return New CachedFrameHandle(Me, item)
                Else
                    ' Размеры изменились. Исключаем из кэша (Eviction).
                    _cacheList.Remove(node)
                    _cacheDict.Remove(key)

                    item.IsEvicted = True

                    ' Безопасное "ленивое удаление".
                    ' Если никто сейчас не читает буфер (RefCount <= 0), освобождаем его немедленно.
                    ' Если кто-то читает, буфер будет освобожден в ReleaseItem.
                    If item.RefCount <= 0 Then
                        If item.PooledData IsNot Nothing Then
                            item.PooledData.Dispose()
                            item.PooledData = Nothing
                        End If
                    End If
                End If
            End If
        End SyncLock
        Return Nothing
    End Function

    Public Sub Add(key As String, pooledData As PooledFrameBuffer, w As Integer, h As Integer)
        SyncLock _cacheLock
            ' Защита от двойного добавления
            If _cacheDict.ContainsKey(key) Then
                pooledData?.Dispose()
                Return
            End If

            ' Логика вытеснения самого старого элемента (LRU)
            If _cacheDict.Count >= _maxSize Then
                Dim oldestNode = _cacheList.First
                Dim oldestItem = oldestNode.Value

                _cacheList.RemoveFirst()
                _cacheDict.Remove(oldestItem.Key)

                oldestItem.IsEvicted = True

                If oldestItem.RefCount <= 0 Then
                    If oldestItem.PooledData IsNot Nothing Then
                        oldestItem.PooledData.Dispose()
                        oldestItem.PooledData = Nothing
                    End If
                End If
            End If

            Dim newItem As New CacheItem With {
                .Key = key,
                .PooledData = pooledData,
                .Width = w,
                .Height = h,
                .RefCount = 0,
                .IsEvicted = False
            }

            Dim newNode = _cacheList.AddLast(newItem)
            _cacheDict.Add(key, newNode)
        End SyncLock
    End Sub

    Protected Friend Sub ReleaseItem(item As CacheItem)
        SyncLock _cacheLock
            item.RefCount -= 1

            ' Финальное освобождение памяти, если элемент был вытеснен из кэша
            ' и последний читающий поток завершил с ним работу.
            If item.IsEvicted AndAlso item.RefCount <= 0 Then
                If item.PooledData IsNot Nothing Then
                    item.PooledData.Dispose()
                    item.PooledData = Nothing
                End If
            End If
        End SyncLock
    End Sub

    Public Sub Clear()
        SyncLock _cacheLock
            For Each item In _cacheList
                item.IsEvicted = True

                If item.RefCount <= 0 Then
                    If item.PooledData IsNot Nothing Then
                        item.PooledData.Dispose()
                        item.PooledData = Nothing
                    End If
                End If
            Next

            _cacheDict.Clear()
            _cacheList.Clear()
        End SyncLock
    End Sub

    Private _disposedValue As Boolean

    Protected Overridable Sub Dispose(disposing As Boolean)
        If Not _disposedValue Then
            If disposing Then
                Clear()
            End If
            _disposedValue = True
        End If
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        Dispose(disposing:=True)
        GC.SuppressFinalize(Me)
    End Sub
End Class