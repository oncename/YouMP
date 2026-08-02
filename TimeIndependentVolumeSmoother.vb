' Path: TimeIndependentVolumeSmoother.vb
Option Strict On
Option Explicit On

Imports System
Imports System.Diagnostics

Public Class TimeIndependentVolumeSmoother
    Private ReadOnly _stopwatch As Stopwatch
    Private _lastTickTimeSeconds As Double
    Private _smoothedVolume As Double
    Private _smoothSpeed As Double

    Public Sub New(initialVolume As Double, Optional smoothSpeed As Double = 30.0)
        _stopwatch = Stopwatch.StartNew()
        _lastTickTimeSeconds = _stopwatch.Elapsed.TotalSeconds

        _smoothedVolume = Math.Max(0.0, Math.Min(1.0, initialVolume))
        _smoothSpeed = Math.Max(0.0, smoothSpeed)
    End Sub

    Public ReadOnly Property SmoothedVolume As Double
        Get
            Return _smoothedVolume
        End Get
    End Property

    Public Property SmoothSpeed As Double
        Get
            Return _smoothSpeed
        End Get
        Set(value As Double)
            _smoothSpeed = Math.Max(0.0, value)
        End Set
    End Property

    Public Function Update(targetVolume As Double) As Double
        Dim currentTimeSeconds As Double = _stopwatch.Elapsed.TotalSeconds
        Dim deltaTime As Double = currentTimeSeconds - _lastTickTimeSeconds
        _lastTickTimeSeconds = currentTimeSeconds

        If deltaTime > 0.1 Then
            deltaTime = 0.1
        End If

        If deltaTime <= 0.0000001 Then
            Return _smoothedVolume
        End If

        ' Независимое от частоты кадров экспоненциальное сглаживание
        Dim lerpFactor As Double = 1.0 - Math.Exp(-_smoothSpeed * deltaTime)

        _smoothedVolume += (targetVolume - _smoothedVolume) * lerpFactor

        If _smoothedVolume < 0.0 Then
            _smoothedVolume = 0.0
        ElseIf _smoothedVolume > 1.0 Then
            _smoothedVolume = 1.0
        End If

        Return _smoothedVolume
    End Function

    Public Sub Reset(instantVolume As Double)
        _lastTickTimeSeconds = _stopwatch.Elapsed.TotalSeconds

        If instantVolume < 0.0 Then
            _smoothedVolume = 0.0
        ElseIf instantVolume > 1.0 Then
            _smoothedVolume = 1.0
        Else
            _smoothedVolume = instantVolume
        End If
    End Sub
End Class