Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Text

Public NotInheritable Class TileDataContainer

    Private Const Magic As String = "TCNT"
    Private Const Version As Integer = 1

    Private Sub New()
    End Sub

    Public Shared Sub Write(containerPath As String, entries As IEnumerable(Of (Key As String, Data As Byte())))
        Dim entryList = entries.ToList()

        Using fs As New FileStream(containerPath, FileMode.Create, FileAccess.Write)
            Using bw As New BinaryWriter(fs)

                bw.Write(Magic.ToCharArray())
                bw.Write(Version)
                bw.Write(entryList.Count)

                Dim headerSize As Long = 4 + 4 + 4
                For Each entry In entryList
                    headerSize += 2 + Encoding.UTF8.GetByteCount(entry.Key) + 8 + 4
                Next

                Dim runningOffset As Long = headerSize
                Dim offsets(entryList.Count - 1) As Long
                For i = 0 To entryList.Count - 1
                    offsets(i) = runningOffset
                    runningOffset += entryList(i).Data.Length
                Next

                For i = 0 To entryList.Count - 1
                    Dim keyBytes = Encoding.UTF8.GetBytes(entryList(i).Key)
                    bw.Write(CUShort(keyBytes.Length))
                    bw.Write(keyBytes)
                    bw.Write(offsets(i))
                    bw.Write(entryList(i).Data.Length)
                Next

                For Each entry In entryList
                    bw.Write(entry.Data)
                Next

            End Using
        End Using
    End Sub

    Public Shared Function OpenRead(containerPath As String) As TileDataContainerReader
        Return New TileDataContainerReader(containerPath)
    End Function

    Public Shared Function TryReadTile(folderPath As String, key As String, ByRef data As Byte()) As Boolean
        Dim containerPath = Path.Combine(folderPath, "DATA.CNT")
        If Not File.Exists(containerPath) Then
            data = Nothing
            Return False
        End If

        Using reader = OpenRead(containerPath)
            Return reader.TryGet(key, data)
        End Using
    End Function

End Class

Public NotInheritable Class TileDataContainerReader
    Implements IDisposable

    Private ReadOnly _fs As FileStream
    Private ReadOnly _index As New Dictionary(Of String, (Offset As Long, Length As Integer))

    Public Sub New(containerPath As String)
        _fs = New FileStream(containerPath, FileMode.Open, FileAccess.Read, FileShare.Read)
        Using br As New BinaryReader(_fs, Encoding.UTF8, True)

            Dim magic = New String(br.ReadChars(4))
            If magic <> "TCNT" Then
                Throw New InvalidDataException($"'{containerPath}' is not a valid TileDataContainer.")
            End If

            Dim version = br.ReadInt32()
            Dim count = br.ReadInt32()

            For i = 0 To count - 1
                Dim keyLen = br.ReadUInt16()
                Dim key = New String(br.ReadChars(keyLen))
                Dim offset = br.ReadInt64()
                Dim length = br.ReadInt32()
                _index(key) = (offset, length)
            Next

        End Using
    End Sub

    Public Function TryGet(key As String, ByRef data As Byte()) As Boolean
        Dim entry As (Offset As Long, Length As Integer)
        If Not _index.TryGetValue(key, entry) Then
            data = Nothing
            Return False
        End If

        data = New Byte(entry.Length - 1) {}
        _fs.Seek(entry.Offset, SeekOrigin.Begin)
        Dim read = 0
        While read < entry.Length
            Dim n = _fs.Read(data, read, entry.Length - read)
            If n = 0 Then Throw New EndOfStreamException("Unexpected end of container file.")
            read += n
        End While
        Return True
    End Function

    Public ReadOnly Property Keys As IEnumerable(Of String)
        Get
            Return _index.Keys
        End Get
    End Property

    Public Sub Dispose() Implements IDisposable.Dispose
        _fs.Dispose()
    End Sub

End Class