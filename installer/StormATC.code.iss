; Pascal code of the StormATC installer: finding MSFS's Community folders (to install the toolbar
; panel there) and downloading the local voice files (Piper, whisper.cpp, a Whisper model and the
; controller voices) from their publishers.

[Code]
const
  StoreUserConfig = '{localappdata}\Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache\UserCfg.opt';
  StoreDefaultCommunity = '{localappdata}\Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache\Packages\Community';
  SteamUserConfig = '{userappdata}\Microsoft Flight Simulator 2024\UserCfg.opt';
  SteamDefaultCommunity = '{userappdata}\Microsoft Flight Simulator 2024\Packages\Community';

var
  DownloadPage: TDownloadWizardPage;
  TargetNames: TStringList;
  TargetRels: TStringList;

// The Community folder: the one UserCfg.opt names, else the usual one, else nothing.
function FindCommunityFolder(const UserConfigFile, DefaultFolder: String): String;
var
  Text: AnsiString;
  Content, Candidate, Key: String;
  Start, Finish: Integer;
begin
  Result := '';
  Candidate := '';
  Key := 'InstalledPackagesPath "';

  if LoadStringFromFile(ExpandConstant(UserConfigFile), Text) then
  begin
    Content := String(Text);
    Start := Pos(Key, Content);
    if Start > 0 then
    begin
      Start := Start + Length(Key);
      Finish := Pos('"', Copy(Content, Start, Length(Content)));
      if Finish > 1 then
        Candidate := RemoveBackslash(Copy(Content, Start, Finish - 1)) + '\Community';
    end;
  end;

  if (Candidate <> '') and DirExists(Candidate) then
    Result := Candidate
  else if DirExists(ExpandConstant(DefaultFolder)) then
    Result := ExpandConstant(DefaultFolder);
end;

function StoreCommunityFolder(Param: String): String;
begin
  Result := FindCommunityFolder(StoreUserConfig, StoreDefaultCommunity);
end;

function SteamCommunityFolder(Param: String): String;
begin
  Result := FindCommunityFolder(SteamUserConfig, SteamDefaultCommunity);
end;

function HasStoreCommunityFolder: Boolean;
begin
  Result := StoreCommunityFolder('') <> '';
end;

function HasSteamCommunityFolder: Boolean;
begin
  Result := SteamCommunityFolder('') <> '';
end;

function FoundAnyCommunityFolder: Boolean;
begin
  Result := HasStoreCommunityFolder or HasSteamCommunityFolder;
end;

function LocalVoiceRoot: String;
begin
  Result := ExpandConstant('{localappdata}\StormATC\local-voice');
end;

// Queues one download unless the file (or what the archive unpacks to) is already there, which
// makes a reinstall quick. Rel is where the file goes under local-voice; an empty Rel marks an
// archive that is unpacked instead (see PlaceLocalVoiceFiles).
procedure AddDownload(const Url, Name, Sha256, Rel: String);
begin
  if Rel <> '' then
  begin
    if FileExists(LocalVoiceRoot + '\' + Rel) then
      Exit;
  end
  else if (Name = 'piper.zip') and FileExists(LocalVoiceRoot + '\piper\piper.exe') then
    Exit
  else if (Name = 'whisper.zip') and FileExists(LocalVoiceRoot + '\whisper\bin\Release\whisper-cli.exe') then
    Exit;

  DownloadPage.Add(Url, Name, Sha256);
  TargetNames.Add(Name);
  TargetRels.Add(Rel);
end;

#include "downloads.inc.iss"

procedure InitializeWizard;
begin
  TargetNames := TStringList.Create;
  TargetRels := TStringList.Create;
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
  DownloadPage.ShowBaseNameInsteadOfUrl := True;
end;

procedure DeinitializeSetup;
begin
  TargetNames.Free;
  TargetRels.Free;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;

  if (CurPageID = wpReady) and WizardIsTaskSelected('localvoice') then
  begin
    DownloadPage.Clear;
    TargetNames.Clear;
    TargetRels.Clear;
    AddLocalVoiceDownloads;

    if TargetNames.Count = 0 then
      Exit;

    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
      except
        if DownloadPage.AbortedByUser then
          Result := False
        else
        begin
          // A refused download (network, or a file that does not match its SHA-256) is not
          // fatal: StormATC works with the Windows voice and the cloud engines.
          TargetNames.Clear;
          TargetRels.Clear;
          Result := SuppressibleMsgBox(
            FmtMessage(CustomMessage('DownloadFailed'), [GetExceptionMessage]),
            mbError, MB_YESNO, IDYES) = IDYES;
        end;
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;

procedure UnpackArchive(const ArchiveName, Destination: String);
var
  ResultCode: Integer;
  Archive: String;
begin
  Archive := ExpandConstant('{tmp}\') + ArchiveName;
  if not FileExists(Archive) then
    Exit;

  ForceDirectories(Destination);
  if (not Exec('powershell.exe',
        '-NoProfile -ExecutionPolicy Bypass -Command "Expand-Archive -LiteralPath ''' + Archive
        + ''' -DestinationPath ''' + Destination + ''' -Force"',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode)) or (ResultCode <> 0) then
    SuppressibleMsgBox(CustomMessage('ExtractFailed'), mbError, MB_OK, IDOK);
end;

// Moves the downloaded files from the temporary folder to local-voice, which StormATC reads on
// its first start to fill in the voice settings.
procedure PlaceLocalVoiceFiles;
var
  I: Integer;
  Name, Rel, Source, Target: String;
begin
  if TargetNames.Count = 0 then
    Exit;

  UnpackArchive('piper.zip', LocalVoiceRoot);
  UnpackArchive('whisper.zip', LocalVoiceRoot + '\whisper\bin');

  for I := 0 to TargetNames.Count - 1 do
  begin
    Name := TargetNames[I];
    Rel := TargetRels[I];
    if Rel = '' then
      Continue;

    Source := ExpandConstant('{tmp}\') + Name;
    Target := LocalVoiceRoot + '\' + Rel;
    if FileExists(Source) then
    begin
      ForceDirectories(ExtractFilePath(Target));
      if FileCopy(Source, Target, False) then
        DeleteFile(Source);
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    PlaceLocalVoiceFiles;
end;
