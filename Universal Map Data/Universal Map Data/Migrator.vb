Imports System.IO
Imports System.Text

Public NotInheritable Class Migrator

    ' This migration is intended and only for CMD2 to UMDv2.00. Other versions are not supported.

    Private Const OLD_MAGIC As String = "CMD2"
    Private Const NEW_MAGIC As String = "UMDv2.00"
    Private Const HEADER_SIZE As Integer = 64
    Private Const COPY_BUFFER_SIZE As Integer = 1024 * 1024 ' 1 MB

    Public Shared Sub Migrate(oldPath As String, newPath As String,
                               Optional progress As Action(Of String) = Nothing,
                               Optional progressPercent As Action(Of Integer) = Nothing)

        Dim tmp = newPath & ".tmp"
        Dim tileCount As Integer

        Try
            progress?.Invoke("Copy data …")
            CopyWithProgress(oldPath, tmp, progressPercent)

            Try
                File.SetAttributes(tmp, FileAttributes.Normal)
            Catch

            End Try

            progress?.Invoke("Rewriting header …")
            Using fs As New FileStream(tmp, FileMode.Open, FileAccess.ReadWrite,
                                        FileShare.None, bufferSize:=HEADER_SIZE)

                Dim oldHeader(HEADER_SIZE - 1) As Byte
                fs.Read(oldHeader, 0, HEADER_SIZE)

                Dim magic = Encoding.ASCII.GetString(oldHeader, 0, 4)
                If magic <> OLD_MAGIC Then
                    Throw New InvalidDataException($"Unexpected MAGIC '{magic}' in {oldPath}")
                End If

                Dim p As Integer = 4
                Dim ver = oldHeader(p) : p += 1
                tileCount = BitConverter.ToInt32(oldHeader, p) : p += 4
                Dim jsonOffset = BitConverter.ToInt64(oldHeader, p) : p += 8
                Dim indexOffset = BitConverter.ToInt64(oldHeader, p) : p += 8
                Dim dataOffset = BitConverter.ToInt64(oldHeader, p) : p += 8
                Dim entrySize = BitConverter.ToInt16(oldHeader, p) : p += 2
                Dim tileSize = BitConverter.ToInt32(oldHeader, p) : p += 4
                Dim compressionMode = oldHeader(p) : p += 1
                Dim gzipLevel = oldHeader(p) : p += 1
                Dim overlaysOffset = BitConverter.ToInt64(oldHeader, p) : p += 8

                Using ms As New MemoryStream(HEADER_SIZE)
                    Using bw As New BinaryWriter(ms)
                        bw.Write(Encoding.ASCII.GetBytes(NEW_MAGIC))
                        bw.Write(ver)
                        bw.Write(tileCount)
                        bw.Write(jsonOffset)
                        bw.Write(indexOffset)
                        bw.Write(dataOffset)
                        bw.Write(entrySize)
                        bw.Write(tileSize)
                        bw.Write(compressionMode)
                        bw.Write(gzipLevel)
                        bw.Write(overlaysOffset)

                        Dim used = CInt(ms.Position)
                        bw.Write(New Byte(HEADER_SIZE - used - 1) {})
                    End Using

                    Dim newHeader = ms.ToArray()
                    If newHeader.Length <> HEADER_SIZE Then
                        Throw New InvalidOperationException("New header does not fit in HEADER_SIZE.")
                    End If

                    fs.Seek(0, SeekOrigin.Begin)
                    fs.Write(newHeader, 0, newHeader.Length)
                    fs.Flush(flushToDisk:=True)
                End Using
            End Using

            progress?.Invoke("Finalizing file …")
            MoveWithRetry(tmp, newPath)

            progress?.Invoke($"Finished – {tileCount} Tiles migrated, no single tile re-encoded.")
            progressPercent?.Invoke(100)

        Catch ex As UnauthorizedAccessException
            TryDeleteTmp(tmp)
            Throw New UnauthorizedAccessException(
                $"Access to '{tmp}' denied. Possible causes: " &
                "(1) The source file is read-only (ReadOnly attribute was inherited), " &
                "(2) An antivirus/backup program has the file locked temporarily, " &
                "(3) The target drive is read-only or requires admin rights, " &
                "(4) The file is still open from a previous failed run. " &
                "Please check target folder permissions, start the app as administrator, " &
                "oder eine evtl. vorhandene .tmp-Datei manuell löschen.", ex)
        Catch
            TryDeleteTmp(tmp)
            Throw
        End Try
    End Sub

    Private Shared Sub CopyWithProgress(sourcePath As String, destPath As String,
                                         progressPercent As Action(Of Integer))
        If File.Exists(destPath) Then
            File.SetAttributes(destPath, FileAttributes.Normal)
            File.Delete(destPath)
        End If

        Dim totalLength = New FileInfo(sourcePath).Length
        Dim buffer(COPY_BUFFER_SIZE - 1) As Byte
        Dim totalRead As Long = 0
        Dim lastReportedPct As Integer = -1

        Using source As New FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read)
            Using dest As New FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None)
                Dim bytesRead As Integer
                Do
                    bytesRead = source.Read(buffer, 0, buffer.Length)
                    If bytesRead <= 0 Then Exit Do
                    dest.Write(buffer, 0, bytesRead)
                    totalRead += bytesRead

                    If totalLength > 0 Then
                        Dim pct = CInt(totalRead * 100L \ totalLength)
                        If pct <> lastReportedPct Then
                            lastReportedPct = pct
                            progressPercent?.Invoke(pct)
                        End If
                    End If
                Loop
            End Using
        End Using
    End Sub

    Private Shared Sub MoveWithRetry(tmp As String, newPath As String)
        Const maxAttempts As Integer = 5

        If File.Exists(newPath) Then
            File.SetAttributes(newPath, FileAttributes.Normal)
            File.Delete(newPath)
        End If

        For attempt = 1 To maxAttempts
            Try
                File.Move(tmp, newPath)
                Return
            Catch ex As IOException When attempt < maxAttempts
                Threading.Thread.Sleep(200 * attempt)
            Catch ex As UnauthorizedAccessException When attempt < maxAttempts
                Try
                    File.SetAttributes(tmp, FileAttributes.Normal)
                Catch
                End Try
                Threading.Thread.Sleep(200 * attempt)
            End Try
        Next

        File.Move(tmp, newPath)
    End Sub

    Private Shared Sub TryDeleteTmp(tmp As String)
        Try
            If File.Exists(tmp) Then
                File.SetAttributes(tmp, FileAttributes.Normal)
                File.Delete(tmp)
            End If
        Catch

        End Try
    End Sub

End Class