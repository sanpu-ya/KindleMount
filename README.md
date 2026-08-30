# KindleMount

USB 接続された Kindle を検知し、**MTP (Media Transfer Protocol)** 経由でドライブレターを
割り当ててマウントする Windows 常駐アプリケーション (C# / .NET 10)。

Paperwhite 第 11 世代以降・Scribe・Colorsoft などの新しい Kindle は USB マスストレージでは
なく MTP で接続されるため、Windows は「ポータブルデバイス」として扱い、**ドライブレターを
割り当てません**。そのため `robocopy`、バックアップツール、Calibre 以外の一般的なアプリから
ファイルを扱えません。KindleMount は MTP を仮想ファイルシステムとして公開し、
`K:\` のような普通のドライブとして見えるようにします。

```
┌──────────────┐   WM_DEVICECHANGE   ┌───────────────┐
│ USB / WPD    ├────────────────────>│ MountService  │
└──────────────┘                     └───────┬───────┘
                                             │
        ┌────────────────────────────────────┴───────────┐
        │                                                │
┌───────▼────────┐   WPD COM (直列化)          ┌──────────▼────────┐
│ MtpConnection  │<───────────────────────────>│ MtpDokanOperations│
│ MtpFileStore   │                             │  (IDokanOperations)│
└───────┬────────┘                             └──────────┬────────┘
        │  取得/転送                                       │ Dokan
┌───────▼────────┐                             ┌──────────▼────────┐
│ ContentCache   │  ローカル実体               │  K:\  (エクスプローラー)│
└────────────────┘                             └───────────────────┘
```

## 必要なもの

| 項目 | 内容 |
| --- | --- |
| OS | Windows 10 1809 以降 / Windows 11 (x64) |
| ランタイム | .NET 10 Desktop Runtime |
| ドライバ | **Dokany 2.x** (仮想ファイルシステムドライバ) |

Dokany のインストール:

```bash
winget install dokan-dev.Dokany
```

インストール後に再起動が必要な場合があります。未導入のまま起動すると、配布ページを開く
かどうかを尋ねるダイアログが表示されます。

## ビルドと実行

```bash
dotnet build -c Release
```

```bash
dotnet run --project src/KindleMount
```

バージョンは `src/KindleMount/KindleMount.csproj` の `<Version>` で決まります。
ここを更新すれば、トレイメニュー・設定ダイアログ・`--scan`・ログの表記がまとめて変わります。

配布用の発行:

```bash
dotnet publish src/KindleMount -c Release -o dist
```

## 使い方

1. `KindleMount.exe` を起動するとタスクトレイに常駐します。
2. Kindle を USB 接続し、Kindle 側の画面ロックを解除します。
3. 自動的に空いているドライブレター (既定では `K:` から順) へマウントされ、
   バルーン通知が出ます。
4. トレイアイコンを右クリックすると、マウント中のドライブを開く・安全に取り外す・
   設定変更などが行えます。ダブルクリックで最初のドライブが開きます。

### 診断モード

デバイスが認識されないときは、コンソールから次を実行すると検出状況を確認できます。

```bash
KindleMount.exe --scan
```

Dokany の導入状況、接続中の MTP デバイス一覧、Kindle 判定の結果、ストレージ構成、
ルート直下のフォルダが表示されます。

## 実機での検証結果

Kindle Paperwhite Signature Edition (`VID_1949&PID_9981`, 26 GB) + Dokany 2.3 / ドライバ 400 で確認済み:

| 項目 | 結果 |
| --- | --- |
| 自動検知 → マウント | 起動から約 0.6 秒で `K:\` にマウント |
| フォルダ列挙 | ルート・`documents` とも正常 (日本語ファイル名を含む) |
| 容量表示 | 22,552 MB 空き / 26,071 MB |
| 書き込み (2 MB) | 約 15 MB/s |
| 読み取り (2 MB) | 約 13 MB/s、SHA256 がラウンドトリップで完全一致 |
| 追記 | バイト単位で完全一致 |
| 改名・削除・フォルダ削除 | 正常 |
| プロセス終了時 | ドライブが自動的にアンマウントされる |

## 設定

`%APPDATA%\KindleMount\settings.json` に保存されます (トレイの「設定...」からも変更可)。

| キー | 既定値 | 説明 |
| --- | --- | --- |
| `preferredDriveLetters` | `KLMNOPQRSTUVWXYZ` | 使用したいドライブレターの優先順 |
| `volumeLabel` | `""` | ドライブに表示する名前。空ならデバイス名 (例: `Kindle` と入れれば固定) |
| `autoMount` | `true` | USB 接続時に自動マウントする |
| `openExplorerOnMount` | `false` | マウント後にエクスプローラーを開く |
| `showNotifications` | `true` | バルーン通知を出す |
| `readOnly` | `false` | 読み取り専用でマウントする |
| `removableDrive` | `true` | リムーバブルドライブとして見せる |
| `useMountManager` | `false` | マウントマネージャー経由でマウントする (下記参照) |
| `directoryCacheSeconds` | `15` | フォルダ一覧のキャッシュ時間 |
| `fileCacheLimitMegabytes` | `4096` | ローカルキャッシュの上限 (0 で無制限) |
| `dokanTimeoutSeconds` | `300` | Dokan の I/O タイムアウト |
| `pollIntervalSeconds` | `15` | 定期スキャン間隔 (0 で無効) |
| `extraDeviceNamePatterns` | `[]` | Kindle 判定に追加する名前の部分一致パターン |
| `allowAnyMtpDevice` | `false` | Kindle 以外の MTP デバイスもマウント対象にする |
| `verboseLogging` | `false` | 詳細ログ + Dokan のデバッグ出力 |

ログ: `%APPDATA%\KindleMount\logs\kindlemount.log`
キャッシュ: `%LOCALAPPDATA%\KindleMount\cache\`

## 設計上の要点

### なぜローカルキャッシュを経由するのか

MTP はファイル単位の転送プロトコルで、**ランダムアクセス読み取りも部分書き込みもできません**。
一方 Windows のファイルシステム API は任意オフセットの読み書きを前提とします。この差を埋める
ため、KindleMount は次のように動作します。

- **読み取り**: 最初の `ReadFile` でファイル全体を `%LOCALAPPDATA%` へ取り出し、
  以降はローカル実体に対して応答する。キャッシュキーにサイズと更新日時を含めるので、
  デバイス側で内容が変われば自動的に再取得される。
- **書き込み**: ローカル実体へ書き、**最後のハンドルが閉じられた時点** (Dokan の `Cleanup`)
  でデバイスへ一括アップロードする。`FlushFileBuffers` ごとに転送すると実用にならないため。
- 書き戻しに失敗した場合、内容が食い違うローカル実体は破棄される。

これは `go-mtpfs` など既存の MTP-FUSE 実装と同じ方針です。

### WPD 呼び出しの直列化

WPD (Windows Portable Devices) は 1 デバイスにつき 1 トランザクションしか処理できません。
Dokan は複数のワーカースレッドからコールバックするため、`MtpConnection` がすべての呼び出しを
ロックで直列化します。長時間の転送でカーネル側タイムアウトに当たらないよう、
`DokanKeepAlive` が定期的に `TryResetTimeout` を呼びます。

### 取り外しをどう検知するか

アンマウントの引き金は 3 通りあり、それぞれ別の経路で拾っています。

1. **アプリから外す** (トレイの「安全な取り外し」/ 終了) — `MountSession.Dispose()` が
   Dokan インスタンスを破棄する通常の経路。
2. **Dokan 側から外される** — `IDokanOperations.Unmounted` が呼ばれる。自分で破棄したときも
   同じコールバックを通るため、`_disposed` を見て外部要因のときだけ `Ejected` を発火し、
   MTP 接続とドライブレターを解放する。アプリ自体は常駐したまま。
3. **デバイスが取り外された** — `WM_DEVICECHANGE` の `DBT_DEVICEREMOVECOMPLETE` を受けて
   再スキャンし、消えたデバイスのマウントを解除する。
   取り外しの**予告** (`DBT_DEVICEQUERYREMOVE` / `DBT_DEVICEREMOVEPENDING`) も拾えるように
   してあり、届いた場合はデバウンスせず即座に解除します。通知のデバイスパスは種類 (USB / WPD)
   で末尾の GUID が変わるため、`KindleDeviceInfo.MatchesDevicePath` で GUID を外して照合します。

いずれの経路でも、取り外した後に自動で再マウントし直すことはありません
(デバイスを挿し直すまで `_autoMountAttempted` が抑止します)。

> 実機で `CM_Request_Device_Eject` (「ハードウェアの安全な取り外し」と同じ API) を試したところ、
> **予告は配送されず** `DBT_DEVICEREMOVECOMPLETE` だけが届きました。また MTP セッションを
> 掴んだままでも取り外しは拒否されません (`VetoType=0`)。予告を受け取るには対象デバイスの
> ファイルハンドルを `DBT_DEVTYP_HANDLE` で登録する必要がありますが、現状の経路 3 で
> 約 1.5 秒後に解除できており実害がないため、そこまでは行っていません。

### ストレージのマッピング

- ストレージが 1 つのデバイス (ほとんどの Kindle) は、そのストレージをドライブ直下へ直接マップ。
  → `K:\documents\book.azw3`
- 複数ある場合はストレージ名を仮想的なトップレベルフォルダとして見せます。
  → `K:\内部ストレージ\documents\book.azw3`

## 既知の制限

- **フォルダの移動**: MTP に一括移動の操作がないため `NotImplemented` を返します。
  エクスプローラーは自動的に「コピー + 削除」へフォールバックします。ファイル単体の移動は
  同一フォルダ内なら改名、別フォルダなら取得→転送→削除で処理します。
- **タイムスタンプ・属性の設定**: MTP 側が決めるため無視します (エラーは返しません)。
- **大きなファイルの初回アクセス**: ファイル全体の転送が終わるまで待ちが発生します。
- **Kindle 側のロック**: 画面がロックされているとストレージが列挙できません。解除してから
  接続してください。
- **エクスプローラーの「取り出し」は効きません**: Dokany 2.3.1 のドライバは
  `IOCTL_STORAGE_EJECT_MEDIA` と `FSCTL_DISMOUNT_VOLUME` を実装しておらず
  (どちらも `ERROR_INVALID_FUNCTION` を返す)、右クリックメニューに項目は出るものの
  何も起きません。`useMountManager` を有効にしても変わりません。
  取り外しはトレイメニューの「安全な取り外し」か、Kindle 本体の USB 取り外しで行ってください。
  なお**何らかの経路で Dokan 側からアンマウントされた場合は検知して後始末します** (下記)。
- **`Get-Volume` に現れない**: 既定では Dokan の仮想ドライブは WMI の `MSFT_Volume` に
  登録されません。`useMountManager` を `true` にすると登録され、`DriveType: Removable` /
  `FileSystem: MTP` として一覧に出ます (この環境では管理者権限は不要でした)。
  エクスプローラーや通常のファイル API からは、どちらの設定でも問題なく見えます。
- Dokany 2.x のカーネルドライバが必要です。

## ソース構成

| パス | 役割 |
| --- | --- |
| `Program.cs` | エントリポイント、単一インスタンス制御、Dokany の存在確認 |
| `AppSettings.cs` / `AppPaths.cs` | 設定と各種パス |
| `AppInfo.cs` | アプリ名とバージョンの表示用文字列 |
| `Devices/DeviceChangeWatcher.cs` | `WM_DEVICECHANGE` 購読 (メッセージ専用ウィンドウ) |
| `Devices/KindleScanner.cs` | WPD デバイス列挙と Kindle 判定 (VID_1949 / 名前) |
| `Mtp/MtpConnection.cs` | WPD セッション管理・呼び出しの直列化・再接続 |
| `Mtp/MtpPathMapper.cs` | Dokan パス ↔ MTP パス変換、ストレージ構成 |
| `Mtp/MtpFileStore.cs` | 列挙キャッシュ、転送、容量取得 |
| `FileSystem/MtpDokanOperations.cs` | `IDokanOperations` 実装 |
| `FileSystem/OpenFile.cs` / `OpenFileTable.cs` | ハンドル共有とローカル実体管理 |
| `FileSystem/ContentCache.cs` | ローカルキャッシュと LRU 整理 |
| `Mounting/MountService.cs` | 検知結果とマウント状態の突き合わせ |
| `Mounting/MountSession.cs` | デバイス 1 台分のマウント/アンマウント |
| `Tray/` | トレイ常駐 UI と設定ダイアログ |
| `Diagnostics/ConsoleDiagnostics.cs` | `--scan` の診断出力 |
