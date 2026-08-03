Option Explicit

Dim shell
Dim fso
Dim baseDir
Dim command

Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
baseDir = fso.GetParentFolderName(WScript.ScriptFullName)
command = "powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File """ & baseDir & "\app-studio.ps1"""
shell.Run command, 0, False

