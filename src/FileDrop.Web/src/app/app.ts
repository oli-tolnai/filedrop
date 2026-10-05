import { HttpClient, HttpErrorResponse, HttpEventType, HttpHeaders, HttpParams, HttpRequest } from '@angular/common/http';
import { Component, inject, OnDestroy, OnInit, signal } from '@angular/core';
import { Capacitor, PluginListenerHandle } from '@capacitor/core';
import * as QRCode from 'qrcode';
import { NativeSharedFile, ShareReceiver } from './share-receiver';

type ViewName = 'download' | 'upload' | 'mine' | 'admin';
type Visibility = 'shared' | 'code';
type UploadMode = 'bundle' | 'separate';

interface StorageStatus {
  fileDropUsedBytes: number;
  diskAvailableBytes: number;
  uploadCapacityBytes: number;
  reservedFreeSpaceBytes: number;
}

interface Share {
  id: string;
  fileName: string;
  title: string | null;
  note: string | null;
  sizeBytes: number;
  visibility: Visibility;
  accessCode: string | null;
  ownerDisplayName: string | null;
  createdAtUtc: string;
  expiresAtUtc: string | null;
  deleteAfterFirstDownload: boolean;
  downloadCount: number;
  downloadUrl: string;
}

interface Account {
  id: string;
  displayName: string;
  isAdmin: boolean;
  deviceName: string;
}

interface AccountStatus {
  setupRequired: boolean;
  account: Account | null;
}

interface OwnedShare {
  share: Share;
  status: 'active' | 'expired' | 'deleted';
}

interface AdminUser {
  id: string;
  displayName: string;
  isAdmin: boolean;
  createdAtUtc: string;
  activeSessionCount: number;
}

interface DroppedEntry {
  isFile: boolean;
  isDirectory: boolean;
  name: string;
}

interface DroppedFileEntry extends DroppedEntry {
  file(success: (file: File) => void, error?: (error: DOMException) => void): void;
}

interface DroppedDirectoryEntry extends DroppedEntry {
  createReader(): {
    readEntries(success: (entries: DroppedEntry[]) => void, error?: (error: DOMException) => void): void;
  };
}

@Component({
  selector: 'app-root',
  styleUrl: './app.css',
  templateUrl: './app.html',
})
export class App implements OnInit, OnDestroy {
  private readonly http = inject(HttpClient);
  private refreshTimer: number | null = null;
  private nativeListeners: PluginListenerHandle[] = [];
  private readonly refreshWhenVisible = () => {
    if (document.visibilityState === 'visible') this.refreshLiveData();
  };

  protected readonly activeView = signal<ViewName>('download');
  protected readonly apiStatus = signal<'checking' | 'online' | 'offline'>('checking');
  protected readonly storageState = signal<'loading' | 'ready' | 'error'>('loading');
  protected readonly storageStatus = signal<StorageStatus | null>(null);
  protected readonly sharedFiles = signal<Share[]>([]);
  protected readonly filesState = signal<'loading' | 'ready' | 'unauthorized' | 'error'>('loading');
  protected readonly selectedFiles = signal<File[]>([]);
  protected readonly nativeSharedFiles = signal<NativeSharedFile[]>([]);
  protected readonly uploadMode = signal<UploadMode>('bundle');
  protected readonly shareTitle = signal('');
  protected readonly shareNote = signal('');
  protected readonly visibility = signal<Visibility>('shared');
  protected readonly expiration = signal('1h');
  protected readonly downloadCode = signal('');
  protected readonly notice = signal('');
  protected readonly uploading = signal(false);
  protected readonly uploadProgress = signal(0);
  protected readonly createdShare = signal<Share | null>(null);
  protected readonly createdShares = signal<Share[]>([]);
  protected readonly qrCodeDataUrl = signal('');
  protected readonly shareToolsShare = signal<Share | null>(null);
  protected readonly shareToolsQrCode = signal('');
  protected readonly accountStatus = signal<AccountStatus | null>(null);
  protected readonly ownedShares = signal<OwnedShare[]>([]);
  protected readonly showHistory = signal(false);
  protected readonly adminUsers = signal<AdminUser[]>([]);
  protected readonly adminState = signal<'idle' | 'loading' | 'ready' | 'error'>('idle');
  protected readonly accountBusy = signal(false);

  protected readonly expirationOptions = [
    { value: 'after-download', label: 'Első sikeres letöltés után' },
    { value: '15m', label: '15 perc' },
    { value: '1h', label: '1 óra' },
    { value: '24h', label: '24 óra' },
    { value: '7d', label: '7 nap' },
    { value: 'manual', label: 'Kézi törlésig' },
  ];

  ngOnInit(): void {
    this.http.get<{ status: string }>('/api/health').subscribe({
      next: (result) => this.apiStatus.set(result.status === 'ok' ? 'online' : 'offline'),
      error: () => this.apiStatus.set('offline'),
    });
    this.loadStorage();
    this.loadAccount();
    this.loadPendingCode();
    void this.loadPendingShareFile();
    void this.initializeNativeShareReceiver();
    this.refreshTimer = window.setInterval(() => this.refreshLiveData(), 5000);
    window.addEventListener('focus', this.refreshWhenVisible);
    document.addEventListener('visibilitychange', this.refreshWhenVisible);
  }

  ngOnDestroy(): void {
    if (this.refreshTimer !== null) window.clearInterval(this.refreshTimer);
    window.removeEventListener('focus', this.refreshWhenVisible);
    document.removeEventListener('visibilitychange', this.refreshWhenVisible);
    for (const listener of this.nativeListeners) void listener.remove();
  }

  protected selectView(view: ViewName): void {
    this.activeView.set(view);
    this.notice.set('');
    if (view === 'mine' && this.accountStatus()?.account) this.loadOwnedShares();
    if (view === 'admin' && this.accountStatus()?.account?.isAdmin) this.loadAdminUsers();
  }

  protected submitSetup(event: SubmitEvent): void {
    event.preventDefault();
    const values = this.formValues(event);
    this.accountRequest('/api/account/setup', {
      displayName: values.get('displayName'),
      password: values.get('password'),
      deviceName: values.get('deviceName'),
      setupToken: values.get('setupToken'),
    }, 'A tulajdonosi fiók elkészült.');
  }

  protected submitLogin(event: SubmitEvent): void {
    event.preventDefault();
    const values = this.formValues(event);
    this.accountRequest('/api/account/login', {
      displayName: values.get('displayName'),
      password: values.get('password'),
      deviceName: values.get('deviceName'),
    }, 'Sikeres bejelentkezés.');
  }

  protected createFamilyAccount(event: SubmitEvent): void {
    event.preventDefault();
    const form = event.currentTarget as HTMLFormElement;
    const values = new FormData(form);
    this.accountBusy.set(true);
    this.http.post('/api/admin/users', {
      displayName: values.get('displayName'),
      password: values.get('password'),
    }).subscribe({
      next: () => {
        this.notice.set('A családtag fiókja elkészült.');
        form.reset();
        this.loadAdminUsers();
      },
      error: (error: HttpErrorResponse) => {
        this.notice.set(this.readError(error, 'A fiók létrehozása nem sikerült.'));
        this.accountBusy.set(false);
      },
      complete: () => this.accountBusy.set(false),
    });
  }

  protected logout(): void {
    this.http.post('/api/account/logout', {}).subscribe({
      next: () => {
        this.accountStatus.update(value => value ? { ...value, account: null } : value);
        this.ownedShares.set([]);
        this.sharedFiles.set([]);
        this.filesState.set('unauthorized');
        this.adminUsers.set([]);
        this.activeView.set('download');
        this.notice.set('Kijelentkeztél.');
      },
      error: () => this.notice.set('A kijelentkezés nem sikerült.'),
    });
  }

  protected deleteOwnShare(item: OwnedShare): void {
    if (!confirm(`A(z) „${item.share.fileName}” fájl azonnal törlődik, és a linkje többé nem működik. Folytatod?`)) return;
    this.http.delete(`/api/me/shares/${item.share.id}`).subscribe({
      next: () => {
        this.notice.set('A megosztást visszavontuk, a fájlt töröltük.');
        this.loadOwnedShares();
        this.loadSharedFiles();
        this.loadStorage();
      },
      error: (error: HttpErrorResponse) => this.notice.set(this.readError(error, 'A visszavonás nem sikerült.')),
    });
  }

  protected ownedStatusLabel(status: OwnedShare['status']): string {
    return status === 'active' ? 'Aktív' : status === 'expired' ? 'Lejárt' : 'Törölve';
  }

  protected visibleOwnedShares(): OwnedShare[] {
    return this.showHistory()
      ? this.ownedShares()
      : this.ownedShares().filter(item => item.status === 'active');
  }

  protected toggleHistory(): void {
    this.showHistory.update(value => !value);
  }

  protected selectFile(event: Event, append = false): void {
    const input = event.target as HTMLInputElement;
    const selected = Array.from(input.files ?? []);
    const candidates = append ? [...this.selectedFiles(), ...selected] : selected;
    const uniqueFiles: File[] = [];
    const seen = new Set<string>();
    for (const file of candidates) {
      const key = this.fileIdentity(file);
      if (seen.has(key)) continue;
      seen.add(key);
      uniqueFiles.push(file);
    }
    const duplicateCount = candidates.length - uniqueFiles.length;
    this.setSelectedFiles(uniqueFiles);
    if (duplicateCount > 0) {
      this.notice.set(duplicateCount === 1
        ? 'Az ismételt fájlt kihagytuk a kijelölésből.'
        : `${duplicateCount} ismételt fájlt kihagytunk a kijelölésből.`);
    }
    // Ugyanazt a fájlt egymás után is lehessen újraválasztani.
    input.value = '';
  }

  protected async dropFile(event: DragEvent): Promise<void> {
    event.preventDefault();
    event.stopPropagation();

    const items = Array.from(event.dataTransfer?.items ?? []);
    const entries = items
      .map(item => {
        const getEntry = (item as unknown as { webkitGetAsEntry?: () => unknown }).webkitGetAsEntry;
        return typeof getEntry === 'function' ? getEntry.call(item) as DroppedEntry | null : null;
      })
      .filter((entry): entry is DroppedEntry => entry !== null);

    if (!entries.length) {
      this.setSelectedFiles(Array.from(event.dataTransfer?.files ?? []));
      return;
    }

    try {
      const files = (await Promise.all(entries.map(entry => this.readDroppedEntry(entry)))).flat();
      this.setSelectedFiles(files);
      if (!files.length) this.notice.set('A behúzott mappa nem tartalmazott olvasható fájlt.');
    } catch {
      this.notice.set('A behúzott mappa tartalma nem olvasható be. Próbáld újra, vagy használd a mappaválasztó gombot.');
    }
  }

  protected keepFileHere(event: DragEvent): void {
    event.preventDefault();
  }

  protected removeSelectedFile(index: number): void {
    const files = this.selectedFiles().filter((_, fileIndex) => fileIndex !== index);
    this.setSelectedFiles(files);
  }

  protected setVisibility(value: Visibility): void {
    this.visibility.set(value);
    this.createdShare.set(null);
    this.createdShares.set([]);
    this.qrCodeDataUrl.set('');
  }

  protected setExpiration(event: Event): void {
    this.expiration.set((event.target as HTMLSelectElement).value);
  }

  protected updateCode(event: Event): void {
    const input = event.target as HTMLInputElement;
    const code = input.value.replace(/[^a-zA-Z0-9]/g, '').toUpperCase().slice(0, 6);
    input.value = code;
    this.downloadCode.set(code);
    this.notice.set('');
  }

  protected updateShareTitle(event: Event): void {
    this.shareTitle.set((event.target as HTMLInputElement).value);
  }

  protected updateShareNote(event: Event): void {
    this.shareNote.set((event.target as HTMLTextAreaElement).value);
  }

  protected setUploadMode(value: UploadMode): void {
    this.uploadMode.set(value);
    this.createdShare.set(null);
    this.createdShares.set([]);
    this.qrCodeDataUrl.set('');
  }

  protected requestUpload(): void {
    const files = this.selectedFiles();
    const nativeFiles = this.nativeSharedFiles();
    if (!this.accountStatus()?.account) {
      this.notice.set('A feltöltéshez előbb be kell jelentkezni.');
      this.activeView.set('mine');
      return;
    }
    if ((!files.length && !nativeFiles.length) || !this.canSelectedFilesFit() || this.uploading()) return;

    this.uploading.set(true);
    this.uploadProgress.set(0);
    this.createdShare.set(null);
    this.createdShares.set([]);
    this.qrCodeDataUrl.set('');
    this.notice.set('');

    if (nativeFiles.length) {
      void this.uploadNativeFiles(nativeFiles);
    } else if (files.length > 1 && this.uploadMode() === 'bundle') {
      this.uploadBundle(files);
    } else {
      this.uploadFilesSequentially(files, 0, 0, this.selectedTotalBytes());
    }
  }

  private async uploadNativeFiles(files: NativeSharedFile[]): Promise<void> {
    const common = {
      serverUrl: window.location.origin,
      visibility: this.visibility(),
      expiration: this.expiration(),
      title: this.shareTitle(),
      note: this.shareNote(),
    };
    try {
      if (files.length > 1 && this.uploadMode() === 'bundle') {
        const requestedName = this.shareTitle().trim() || 'filedrop-csomag';
        const result = await ShareReceiver.uploadBundle({
          ...common,
          bundleName: requestedName.toLowerCase().endsWith('.zip') ? requestedName : `${requestedName}.zip`,
        });
        const share = JSON.parse(result.response) as Share;
        this.createdShare.set(share);
        this.createdShares.set([share]);
        void this.createQrCode(this.getShareLink(share));
        this.notice.set(`${files.length} fájl egy ZIP-csomagban elkészült.`);
      } else {
        let completedBytes = 0;
        for (const file of files) {
          const result = await ShareReceiver.uploadFile({ ...common, id: file.id, title: files.length === 1 ? this.shareTitle() : file.name });
          const share = JSON.parse(result.response) as Share;
          this.createdShares.update(items => [...items, share]);
          if (files.length === 1) {
            this.createdShare.set(share);
            void this.createQrCode(this.getShareLink(share));
          }
          completedBytes += file.size;
          this.uploadProgress.set(Math.round((completedBytes / this.selectedTotalBytes()) * 100));
        }
        this.notice.set(files.length === 1 ? 'A fájl feltöltése elkészült.' : `${files.length} külön fájl feltöltése elkészült.`);
      }
      this.nativeSharedFiles.set([]);
      this.uploadProgress.set(100);
      this.loadSharedFiles(true);
      this.loadOwnedShares(true);
      this.loadStorage(true);
    } catch (error) {
      const message = error instanceof Error ? error.message : 'A telefonról indított feltöltés nem sikerült.';
      this.notice.set(message);
    } finally {
      this.uploading.set(false);
    }
  }

  private uploadBundle(files: File[]): void {
    const form = new FormData();
    for (const file of files) {
      const path = file.webkitRelativePath || file.name;
      form.append(`file:${encodeURIComponent(path)}`, file, file.name);
    }

    const firstPath = files[0]?.webkitRelativePath || '';
    const folderName = firstPath.includes('/') ? firstPath.split('/')[0] : '';
    const requestedName = this.shareTitle().trim() || folderName || 'filedrop-csomag';
    const bundleName = requestedName.toLowerCase().endsWith('.zip') ? requestedName : `${requestedName}.zip`;
    const parameters = new HttpParams()
      .set('visibility', this.visibility())
      .set('expiration', this.expiration())
      .set('title', this.shareTitle())
      .set('note', this.shareNote())
      .set('bundleName', bundleName);
    const request = new HttpRequest('POST', `/api/shares/bundle?${parameters.toString()}`, form, {
      reportProgress: true,
      responseType: 'json',
    });

    this.http.request<Share>(request).subscribe({
      next: (event) => {
        if (event.type === HttpEventType.UploadProgress) {
          const total = event.total || this.selectedTotalBytes();
          this.uploadProgress.set(total > 0 ? Math.min(100, Math.round((event.loaded / total) * 100)) : 0);
        }
        if (event.type === HttpEventType.Response && event.body) {
          this.createdShare.set(event.body);
          this.createdShares.set([event.body]);
          this.uploading.set(false);
          this.uploadProgress.set(100);
          this.notice.set(`${files.length} fájl egy ZIP-csomagban elkészült.`);
          void this.createQrCode(this.getShareLink(event.body));
          this.loadSharedFiles(true);
          this.loadOwnedShares(true);
          this.loadStorage(true);
        }
      },
      error: (error: HttpErrorResponse) => {
        this.notice.set(this.readError(error, 'A ZIP-csomag feltöltése nem sikerült.'));
        this.uploading.set(false);
      },
    });
  }

  private uploadFilesSequentially(files: File[], index: number, completedBytes: number, totalBytes: number): void {
    if (index >= files.length) {
      this.uploading.set(false);
      this.uploadProgress.set(100);
      this.notice.set(files.length === 1 ? 'A fájl feltöltése elkészült.' : `${files.length} fájl feltöltése elkészült.`);
      this.loadSharedFiles(true);
      this.loadOwnedShares(true);
      this.loadStorage(true);
      return;
    }

    const file = files[index];
    const relativePath = file.webkitRelativePath || file.name;
    const automaticTitle = files.length > 1 && relativePath !== file.name ? relativePath.slice(0, 120) : '';

    const parameters = new HttpParams()
      .set('fileName', file.name)
      .set('visibility', this.visibility())
      .set('expiration', this.expiration())
      .set('title', files.length === 1 ? this.shareTitle() : automaticTitle)
      .set('note', this.shareNote());
    const request = new HttpRequest('POST', `/api/shares?${parameters.toString()}`, file, {
      headers: new HttpHeaders({ 'Content-Type': file.type || 'application/octet-stream' }),
      reportProgress: true,
      responseType: 'json',
    });

    this.http.request<Share>(request).subscribe({
      next: (event) => {
        if (event.type === HttpEventType.UploadProgress) {
          const loaded = Math.min(event.loaded, file.size);
          this.uploadProgress.set(totalBytes > 0
            ? Math.min(100, Math.round(((completedBytes + loaded) / totalBytes) * 100))
            : 0);
        }

        if (event.type === HttpEventType.Response && event.body) {
          this.createdShares.update(items => [...items, event.body!]);
          if (files.length === 1) {
            this.createdShare.set(event.body);
            void this.createQrCode(this.getShareLink(event.body));
          }
          this.uploadFilesSequentially(files, index + 1, completedBytes + file.size, totalBytes);
        }
      },
      error: (error: HttpErrorResponse) => {
        const completed = this.createdShares().length;
        const prefix = completed ? `${completed} fájl elkészült. ` : '';
        this.notice.set(prefix + this.readError(error, `A(z) „${relativePath}” feltöltése nem sikerült.`));
        this.uploading.set(false);
      },
    });
  }

  protected requestDownload(): void {
    if (![4, 6].includes(this.downloadCode().length)) return;

    this.http.post<Share>('/api/shares/resolve-code', { code: this.downloadCode() }).subscribe({
      next: (share) => {
        this.downloadCode.set('');
        window.location.assign(share.downloadUrl);
      },
      error: (error: HttpErrorResponse) => {
        this.notice.set(this.readError(error, 'A megosztási kód nem használható.'));
      },
    });
  }

  protected canResolveCode(): boolean {
    return [4, 6].includes(this.downloadCode().length);
  }

  protected downloadShare(share: Share): void {
    window.location.assign(share.downloadUrl);
  }

  protected async copyShareLink(share: Share): Promise<void> {
    const link = this.getShareLink(share);
    try {
      await navigator.clipboard.writeText(link);
    } catch {
      const input = document.createElement('textarea');
      input.value = link;
      input.style.position = 'fixed';
      input.style.opacity = '0';
      document.body.appendChild(input);
      input.select();
      document.execCommand('copy');
      input.remove();
    }
    this.notice.set('A letöltési linket a vágólapra másoltuk.');
  }

  protected async copyShareCode(code: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(code);
    } catch {
      const input = document.createElement('textarea');
      input.value = code;
      input.style.position = 'fixed';
      input.style.opacity = '0';
      document.body.appendChild(input);
      input.select();
      document.execCommand('copy');
      input.remove();
    }
    this.notice.set('A megosztási kódot a vágólapra másoltuk.');
  }

  protected restoreShareTools(share: Share): void {
    this.shareToolsShare.set(share);
    this.shareToolsQrCode.set('');
    void this.createQrCode(this.getShareLink(share), 'tools');
  }

  protected closeShareTools(): void {
    this.shareToolsShare.set(null);
    this.shareToolsQrCode.set('');
  }

  protected selectedTotalBytes(): number {
    const nativeFiles = this.nativeSharedFiles();
    return (nativeFiles.length ? nativeFiles : this.selectedFiles()).reduce((total, file) => total + file.size, 0);
  }

  protected selectedItemCount(): number {
    return this.nativeSharedFiles().length || this.selectedFiles().length;
  }

  protected canSelectedFilesFit(): boolean {
    const files = this.nativeSharedFiles().length ? this.nativeSharedFiles() : this.selectedFiles();
    const storage = this.storageStatus();
    if (!files.length || !storage) return false;
    return this.selectedTotalBytes() <= storage.uploadCapacityBytes;
  }

  protected formatSize(bytes: number): string {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`;
    if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
    return `${(bytes / (1024 * 1024 * 1024)).toFixed(2)} GB`;
  }

  protected formatExpiry(share: Share): string {
    if (share.deleteAfterFirstDownload) return 'Első letöltésig';
    if (!share.expiresAtUtc) return 'Kézi törlésig';
    const timestamp = share.expiresAtUtc.endsWith('Z') ? share.expiresAtUtc : `${share.expiresAtUtc}Z`;
    return new Intl.DateTimeFormat('hu-HU', {
      month: 'short',
      day: 'numeric',
      hour: '2-digit',
      minute: '2-digit',
    }).format(new Date(timestamp));
  }

  protected getAbsoluteDownloadUrl(downloadUrl: string): string {
    return new URL(downloadUrl, window.location.origin).toString();
  }

  protected getShareLink(share: Share): string {
    return share.accessCode
      ? new URL(`/?code=${encodeURIComponent(share.accessCode)}`, window.location.origin).toString()
      : this.getAbsoluteDownloadUrl(share.downloadUrl);
  }

  protected formatDate(timestamp: string): string {
    const normalized = timestamp.endsWith('Z') ? timestamp : `${timestamp}Z`;
    return new Intl.DateTimeFormat('hu-HU', {
      year: 'numeric',
      month: 'short',
      day: 'numeric',
    }).format(new Date(normalized));
  }

  private setSelectedFiles(files: File[]): void {
    this.nativeSharedFiles.set([]);
    if (Capacitor.isNativePlatform()) void ShareReceiver.clearPendingFiles();
    this.selectedFiles.set(files);
    if (files.length > 1) this.uploadMode.set('bundle');
    this.shareTitle.set('');
    this.shareNote.set('');
    this.createdShare.set(null);
    this.createdShares.set([]);
    this.qrCodeDataUrl.set('');
    this.notice.set('');

    const storage = this.storageStatus();
    if (files.length && storage && this.selectedTotalBytes() > storage.uploadCapacityBytes) {
      this.notice.set('A kiválasztott fájlokhoz nincs elég hely a 100 GB-os biztonsági tartalék megtartásával.');
    }
  }

  private async readDroppedEntry(entry: DroppedEntry, parentPath = ''): Promise<File[]> {
    const relativePath = parentPath ? `${parentPath}/${entry.name}` : entry.name;
    if (entry.isFile) {
      const fileEntry = entry as DroppedFileEntry;
      const file = await new Promise<File>((resolve, reject) => fileEntry.file(resolve, reject));
      return [this.withRelativePath(file, relativePath)];
    }

    if (!entry.isDirectory) return [];
    const directoryEntry = entry as DroppedDirectoryEntry;
    const reader = directoryEntry.createReader();
    const children: DroppedEntry[] = [];
    while (true) {
      const batch = await new Promise<DroppedEntry[]>((resolve, reject) => reader.readEntries(resolve, reject));
      if (!batch.length) break;
      children.push(...batch);
    }
    return (await Promise.all(children.map(child => this.readDroppedEntry(child, relativePath)))).flat();
  }

  private withRelativePath(file: File, relativePath: string): File {
    const copy = new File([file], file.name, { type: file.type, lastModified: file.lastModified });
    Object.defineProperty(copy, 'webkitRelativePath', { value: relativePath, enumerable: true });
    return copy;
  }

  private fileIdentity(file: File): string {
    return `${file.webkitRelativePath || file.name}\u0000${file.size}\u0000${file.lastModified}`;
  }

  private loadStorage(silent = false): void {
    if (!silent) this.storageState.set('loading');
    this.http.get<StorageStatus>('/api/storage').subscribe({
      next: (result) => {
        this.storageStatus.set(result);
        this.storageState.set('ready');
      },
      error: () => { if (!silent) this.storageState.set('error'); },
    });
  }

  private loadSharedFiles(silent = false): void {
    if (!silent) this.filesState.set('loading');
    this.http.get<Share[]>('/api/shares').subscribe({
      next: (files) => {
        this.sharedFiles.set(files);
        this.filesState.set('ready');
      },
      error: (error: HttpErrorResponse) => {
        if (!silent || error.status === 401) {
          this.sharedFiles.set([]);
          this.filesState.set(error.status === 401 ? 'unauthorized' : 'error');
        }
      },
    });
  }

  private loadAccount(): void {
    this.http.get<AccountStatus>('/api/account').subscribe({
      next: (status) => {
        this.accountStatus.set(status);
        if (status.account) {
          this.loadOwnedShares();
          this.loadSharedFiles();
          if (status.account.isAdmin) this.loadAdminUsers();
        } else {
          this.filesState.set('unauthorized');
        }
      },
      error: () => {
        this.accountStatus.set({ setupRequired: false, account: null });
        this.filesState.set('unauthorized');
      },
    });
  }

  private loadOwnedShares(silent = false): void {
    this.http.get<OwnedShare[]>('/api/me/shares').subscribe({
      next: (items) => this.ownedShares.set(items),
      error: () => { if (!silent) this.ownedShares.set([]); },
    });
  }

  private refreshLiveData(): void {
    if (document.visibilityState !== 'visible' || !this.accountStatus()?.account) return;
    this.loadSharedFiles(true);
    this.loadOwnedShares(true);
    if (this.activeView() === 'upload') this.loadStorage(true);
  }

  private loadAdminUsers(): void {
    if (!this.accountStatus()?.account?.isAdmin) return;
    this.adminState.set('loading');
    this.http.get<AdminUser[]>('/api/admin/users').subscribe({
      next: (users) => {
        this.adminUsers.set(users);
        this.adminState.set('ready');
      },
      error: () => this.adminState.set('error'),
    });
  }

  private loadPendingCode(): void {
    const code = new URLSearchParams(window.location.search).get('code')?.trim().toUpperCase();
    if (!code || !/^[A-Z0-9]{4,6}$/.test(code)) return;
    this.downloadCode.set(code);
    window.history.replaceState({}, document.title, window.location.pathname);
    window.setTimeout(() => this.requestDownload(), 0);
  }

  private async initializeNativeShareReceiver(): Promise<void> {
    if (!Capacitor.isNativePlatform()) return;
    try {
      const shareListener = await ShareReceiver.addListener('shareReceived', result => this.acceptNativeFiles(result.files));
      const progressListener = await ShareReceiver.addListener('uploadProgress', progress => {
        this.uploadProgress.set(progress.total > 0 ? Math.min(100, Math.round((progress.loaded / progress.total) * 100)) : 0);
      });
      this.nativeListeners.push(shareListener, progressListener);
      const pending = await ShareReceiver.getPendingFiles();
      if (pending.files.length) this.acceptNativeFiles(pending.files);
    } catch {
      this.notice.set('A telefon Megosztás menüjéből érkező fájlokat most nem sikerült átvenni.');
    }
  }

  private acceptNativeFiles(files: NativeSharedFile[]): void {
    if (!files.length) return;
    this.selectedFiles.set([]);
    this.nativeSharedFiles.set(files);
    this.uploadMode.set(files.length > 1 ? 'bundle' : 'separate');
    this.shareTitle.set('');
    this.shareNote.set('');
    this.createdShare.set(null);
    this.createdShares.set([]);
    this.qrCodeDataUrl.set('');
    this.activeView.set('upload');
    this.notice.set(files.length === 1
      ? 'A Megosztás menüből érkező fájl készen áll a feltöltésre.'
      : `${files.length} fájl érkezett a Megosztás menüből. Válaszd ki, hogy egy csomag vagy külön megosztások legyenek.`);
  }

  private async loadPendingShareFile(): Promise<void> {
    if (!new URLSearchParams(window.location.search).has('shared')) return;
    try {
      const db = await this.openShareDatabase();
      const entry = await new Promise<{ name: string; type: string; lastModified: number; blob: Blob } | undefined>((resolve, reject) => {
        const request = db.transaction('pending', 'readonly').objectStore('pending').get('latest');
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
      });
      db.close();
      if (!entry) return;
      const file = new File([entry.blob], entry.name, { type: entry.type, lastModified: entry.lastModified });
      this.setSelectedFiles([file]);
      this.activeView.set('upload');
      this.notice.set('A megosztásmenüből érkező fájl készen áll a feltöltésre.');
      await this.deletePendingShareFile();
    } catch {
      this.notice.set('A megosztásmenüből érkező fájl nem tölthető be.');
    } finally {
      window.history.replaceState({}, document.title, window.location.pathname);
    }
  }

  private openShareDatabase(): Promise<IDBDatabase> {
    return new Promise((resolve, reject) => {
      const request = indexedDB.open('filedrop-share-target', 1);
      request.onupgradeneeded = () => request.result.createObjectStore('pending', { keyPath: 'id' });
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error);
    });
  }

  private async deletePendingShareFile(): Promise<void> {
    const db = await this.openShareDatabase();
    await new Promise<void>((resolve, reject) => {
      const transaction = db.transaction('pending', 'readwrite');
      transaction.objectStore('pending').delete('latest');
      transaction.oncomplete = () => resolve();
      transaction.onerror = () => reject(transaction.error);
    });
    db.close();
  }

  private accountRequest(url: string, body: object, successMessage: string): void {
    this.accountBusy.set(true);
    this.http.post<Account>(url, body).subscribe({
      next: (account) => {
        this.accountStatus.set({ setupRequired: false, account });
        this.notice.set(successMessage);
        this.loadOwnedShares();
        this.loadSharedFiles();
        if (account.isAdmin) this.loadAdminUsers();
      },
      error: (error: HttpErrorResponse) => {
        this.notice.set(error.status === 401
          ? 'A név, a jelszó vagy a beállítási kód hibás.'
          : this.readError(error, 'A művelet nem sikerült.'));
        this.accountBusy.set(false);
      },
      complete: () => this.accountBusy.set(false),
    });
  }

  private formValues(event: SubmitEvent): FormData {
    return new FormData(event.currentTarget as HTMLFormElement);
  }

  private async createQrCode(downloadUrl: string, target: 'upload' | 'tools' = 'upload'): Promise<void> {
    const dataUrl = await QRCode.toDataURL(this.getAbsoluteDownloadUrl(downloadUrl), {
      errorCorrectionLevel: 'M',
      margin: 2,
      width: 220,
      color: { dark: '#172b3a', light: '#ffffff' },
    });
    if (target === 'tools') this.shareToolsQrCode.set(dataUrl);
    else this.qrCodeDataUrl.set(dataUrl);
  }

  private readError(error: HttpErrorResponse, fallback: string): string {
    if (typeof error.error === 'object' && error.error && 'message' in error.error) {
      return String(error.error.message);
    }
    return fallback;
  }
}
