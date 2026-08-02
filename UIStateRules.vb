' Path: UIStateRules.vb
Option Strict On
Option Explicit On

Public Enum TargetFormatType
    Video
    Audio
    Image
End Enum

Public Class MediaInputState
    Public Property HasImage As Boolean
    Public Property HasAudio As Boolean
    Public Property HasVideoWithAudio As Boolean
    Public Property HasVideoNoAudio As Boolean
    Public Property IsAudioReplaced As Boolean

    Public ReadOnly Property EffectiveHasAudio As Boolean
        Get
            Return HasAudio OrElse HasVideoWithAudio OrElse IsAudioReplaced
        End Get
    End Property

    Public ReadOnly Property EffectiveHasVideoWithAudio As Boolean
        Get
            Return HasVideoWithAudio OrElse (HasVideoNoAudio AndAlso IsAudioReplaced)
        End Get
    End Property

    Public ReadOnly Property EffectiveHasVideoNoAudio As Boolean
        Get
            Return HasVideoNoAudio AndAlso Not IsAudioReplaced
        End Get
    End Property
End Class

Public Class UIControlsState
    Public Property CanExport As Boolean
    Public Property CanSelectHardware As Boolean
    Public Property CanSelectEncoder As Boolean
    Public Property CanSelectCompression As Boolean
    Public Property CanSelectResolution As Boolean
End Class

Public Class UIStateRules
    Public Shared Function Evaluate(input As MediaInputState, target As TargetFormatType) As UIControlsState
        Dim state As New UIControlsState With {
            .CanExport = True,
            .CanSelectHardware = True,
            .CanSelectEncoder = True,
            .CanSelectCompression = True,
            .CanSelectResolution = True
        }

        Select Case target
            Case TargetFormatType.Audio
                state.CanSelectHardware = False
                state.CanSelectResolution = False
                state.CanSelectEncoder = True
                state.CanSelectCompression = True
                state.CanExport = input.EffectiveHasAudio AndAlso Not input.HasImage

            Case TargetFormatType.Image
                state.CanSelectHardware = False
                state.CanSelectEncoder = True
                state.CanSelectCompression = True
                state.CanSelectResolution = True

                If input.EffectiveHasAudio AndAlso Not input.HasImage AndAlso
                   Not input.EffectiveHasVideoWithAudio AndAlso Not input.EffectiveHasVideoNoAudio Then
                    state.CanExport = False
                    state.CanSelectCompression = False
                    state.CanSelectResolution = False
                End If

            Case TargetFormatType.Video
                If input.HasImage AndAlso Not input.EffectiveHasAudio Then
                    state.CanExport = False
                    state.CanSelectHardware = False
                    state.CanSelectEncoder = False
                    state.CanSelectCompression = False
                    state.CanSelectResolution = False
                End If
        End Select

        Return state
    End Function
End Class