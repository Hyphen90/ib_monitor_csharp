SetTitleMatchMode, RegEx


/*
for index, param in A_Args
{
    MsgBox % "Parameter " index ": " param
}
*/

if (A_Args.Length() < 2) {
    MsgBox Mindestens zwei Parameter werden benötigt!
    ExitApp
}


param := A_Args[1] . " " . A_Args[2]

;RunWait, "C:\Program Files\obs-studio\OBSCommand\OBSCommand.exe" /server=127.0.0.1:4455 /password=test123 /command="CreateRecordChapter,chapterName=" %param%,,Hide

fullCommand := "/server=127.0.0.1:4455 /password=test123 /command=""CreateRecordChapter,chapterName=" . param . """"
RunWait, %ComSpec% /c ""C:\Program Files\obs-studio\OBSCommand\OBSCommand.exe" %fullCommand%", , Hide


if WinExist("@SMART ahk_exe tws.exe")
{
    WinActivate
	
	if WinExist("Interactive Brokers ahk_exe tws.exe")
    {
        WinActivate
	}
}


		
