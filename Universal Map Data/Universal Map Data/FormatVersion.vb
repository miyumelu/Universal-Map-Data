Imports System.Text
Imports System.Text.RegularExpressions

Public NotInheritable Class FormatVersion

    Private Sub New()

    End Sub

    Public Const MAGIC_PREFIX As String = "UMDv"


    Public Const MAGIC_FIELD_SIZE As Integer = 8


    Public Const SUPPORTED_MAJOR As Integer = 2

    Public Const SUPPORTED_MINOR As Integer = 0

    Public Enum CompatibilityStatus
        Ok
        NeedsAppUpdate
        Incompatible
    End Enum

    Public NotInheritable Class CompatibilityResult
        Public Property Status As CompatibilityStatus
        Public Property Message As String
        Public Property FileMajor As Integer
        Public Property FileMinor As Integer
    End Class

    Public Shared Function CurrentMagic() As String
        Return BuildMagic(SUPPORTED_MAJOR, SUPPORTED_MINOR)
    End Function

    Public Shared Function BuildMagic(major As Integer, minor As Integer) As String
        Dim s = $"{MAGIC_PREFIX}{major}.{minor:D2}"
        If s.Length > MAGIC_FIELD_SIZE Then
            Throw New InvalidOperationException(
                $"MAGIC '{s}' exceeds MAGIC_FIELD_SIZE ({MAGIC_FIELD_SIZE}). Version is too high for the current field format.")
        End If
        Return s
    End Function

    Public Shared Function MagicBytes(magic As String) As Byte()
        Dim raw = Encoding.ASCII.GetBytes(magic)
        Dim buf(MAGIC_FIELD_SIZE - 1) As Byte
        Array.Copy(raw, buf, Math.Min(raw.Length, MAGIC_FIELD_SIZE))
        Return buf
    End Function

    Public Shared Function ParseMagic(magicRaw As String, ByRef major As Integer, ByRef minor As Integer) As Boolean
        major = 0 : minor = 0
        If Not magicRaw.StartsWith(MAGIC_PREFIX) Then Return False

        Dim versionPart = magicRaw.Substring(MAGIC_PREFIX.Length)
        Dim m = Regex.Match(versionPart, "^(\d+)\.(\d+)$")
        If Not m.Success Then Return False

        major = Integer.Parse(m.Groups(1).Value)
        minor = Integer.Parse(m.Groups(2).Value)
        Return True
    End Function

    Public Shared Function CheckCompatibility(magicRaw As String, fileName As String) As CompatibilityResult

        If magicRaw.StartsWith("CMD1") OrElse magicRaw.StartsWith("CMD2") Then
            Return New CompatibilityResult With {
                .Status = CompatibilityStatus.Incompatible,
                .Message = $"The inserted medium contains data for an older version of the navigation system and is incompatible."
            }
        End If

        Dim fileMajor As Integer, fileMinor As Integer
        If Not ParseMagic(magicRaw, fileMajor, fileMinor) Then
            Return New CompatibilityResult With {
                .Status = CompatibilityStatus.Incompatible,
                .Message = $"The inserted medium has an unknown format and is incompatible."
            }
        End If

        If fileMajor <> SUPPORTED_MAJOR Then
            Return New CompatibilityResult With {
                .Status = CompatibilityStatus.Incompatible,
                .Message = $"The inserted medium contains data for a different version of the navigation system. " &
                            $"Only version {SUPPORTED_MAJOR}.xx is supported.",
                .FileMajor = fileMajor, .FileMinor = fileMinor
            }
        End If

        If fileMinor > SUPPORTED_MINOR Then
            Return New CompatibilityResult With {
                .Status = CompatibilityStatus.NeedsAppUpdate,
                .Message = $"The inserted medium requires a newer version of the navigation system software. " &
                            $"The current version supports up to version {SUPPORTED_MAJOR}.{SUPPORTED_MINOR:D2}. Please update you navigation system.",
                .FileMajor = fileMajor, .FileMinor = fileMinor
            }
        End If

        Return New CompatibilityResult With {
            .Status = CompatibilityStatus.Ok,
            .FileMajor = fileMajor, .FileMinor = fileMinor
        }
    End Function

End Class