import { HttpClient, HttpErrorResponse, HttpEventType, HttpHeaders, HttpParams, HttpRequest } from '@angular/common/http';
import { Component, inject, OnInit, signal } from '@angular/core';
import * as QRCode from 'qrcode';

type ViewName = 'files' | 'mine' | 'admin';
type Visibility = 'shared' | 'code';

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

@Component({
  selector: 'app-root',
  styleUrl: './app.css',
  templateUrl: './app.html',
})
export class App implements OnInit {
  private readonly http = inject(HttpClient);

  protected readonly activeView = signal<ViewName>('files');
  protected readonly apiStatus = signal<'checking' | 'online' | 'offline'>('checking');
  protected readonly storageState = signal<'loading' | 'ready' | 'error'>('loading');
  protected readonly storageStatus = signal<StorageStatus | null>(null);
  protected readonly sharedFiles = signal<Share[]>([]);
  protected readonly filesState = signal<'loading' | 'ready' | 'unauthorized' | 'error'>('loading');
  protected readonly selectedFile = signal<File | null>(null);
  protected readonly shareTitle = signal('');
  protected readonly shareNote = signal('');
  protected readonly visibility = signal<Visibility>('shared');
  protected readonly expiration = signal('1h');
  protected readonly downloadCode = signal('');
  protected readonly notice = signal('');
  protected readonly uploading = signal(false);
  protected readonly uploadProgress = signal(0);
  protected readonly createdShare = signal<Share | null>(null);
  protected readonly qrCodeDataUrl = signal('');
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
        this.activeView.set('files');
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

  protected selectFile(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.setSelectedFile(input.files?.item(0) ?? null);
  }

  protected dropFile(event: DragEvent): void {
    event.preventDefault();
    this.setSelectedFile(event.dataTransfer?.files.item(0) ?? null);
  }

  protected keepFileHere(event: DragEvent): void {
    event.preventDefault();
  }

  protected setVisibility(value: Visibility): void {
    this.visibility.set(value);
    this.createdShare.set(null);
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

  protected requestUpload(): void {
    const file = this.selectedFile();
    if (!this.accountStatus()?.account) {
      this.notice.set('A feltöltéshez előbb be kell jelentkezni.');
      this.activeView.set('mine');
      return;
    }
    if (!file || !this.canSelectedFileFit() || this.uploading()) return;

    this.uploading.set(true);
    this.uploadProgress.set(0);
    this.createdShare.set(null);
    this.qrCodeDataUrl.set('');
    this.notice.set('');

    const parameters = new HttpParams()
      .set('fileName', file.name)
      .set('visibility', this.visibility())
      .set('expiration', this.expiration())
      .set('title', this.shareTitle())
      .set('note', this.shareNote());
    const request = new HttpRequest('POST', `/api/shares?${parameters.toString()}`, file, {
      headers: new HttpHeaders({ 'Content-Type': file.type || 'application/octet-stream' }),
      reportProgress: true,
      responseType: 'json',
    });

    this.http.request<Share>(request).subscribe({
      next: (event) => {
        if (event.type === HttpEventType.UploadProgress) {
          const total = event.total ?? file.size;
          this.uploadProgress.set(total > 0 ? Math.min(100, Math.round((event.loaded / total) * 100)) : 0);
        }

        if (event.type === HttpEventType.Response && event.body) {
          this.uploadProgress.set(100);
          this.createdShare.set(event.body);
          this.notice.set('A fájl feltöltése elkészült.');
          void this.createQrCode(this.getShareLink(event.body));
          this.loadSharedFiles();
          this.loadOwnedShares();
          this.loadStorage();
        }
      },
      error: (error: HttpErrorResponse) => {
        this.notice.set(this.readError(error, 'A feltöltés nem sikerült.'));
        this.uploading.set(false);
      },
      complete: () => this.uploading.set(false),
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
    this.createdShare.set(share);
    this.notice.set('A megosztás linkje és QR-kódja újra megnyitható.');
    void this.createQrCode(this.getShareLink(share));
    document.querySelector('.upload-panel')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }

  protected canSelectedFileFit(): boolean {
    const file = this.selectedFile();
    const storage = this.storageStatus();
    if (!file || !storage) return false;
    return file.size <= storage.uploadCapacityBytes;
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

  private setSelectedFile(file: File | null): void {
    this.selectedFile.set(file);
    this.shareTitle.set('');
    this.shareNote.set('');
    this.createdShare.set(null);
    this.qrCodeDataUrl.set('');
    this.notice.set('');

    const storage = this.storageStatus();
    if (file && storage && file.size > storage.uploadCapacityBytes) {
      this.notice.set('Ehhez a fájlhoz nincs elég hely a 100 GB-os biztonsági tartalék megtartásával.');
    }
  }

  private loadStorage(): void {
    this.storageState.set('loading');
    this.http.get<StorageStatus>('/api/storage').subscribe({
      next: (result) => {
        this.storageStatus.set(result);
        this.storageState.set('ready');
      },
      error: () => this.storageState.set('error'),
    });
  }

  private loadSharedFiles(): void {
    this.filesState.set('loading');
    this.http.get<Share[]>('/api/shares').subscribe({
      next: (files) => {
        this.sharedFiles.set(files);
        this.filesState.set('ready');
      },
      error: (error: HttpErrorResponse) => {
        this.sharedFiles.set([]);
        this.filesState.set(error.status === 401 ? 'unauthorized' : 'error');
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

  private loadOwnedShares(): void {
    this.http.get<OwnedShare[]>('/api/me/shares').subscribe({
      next: (items) => this.ownedShares.set(items),
      error: () => this.ownedShares.set([]),
    });
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
      this.setSelectedFile(file);
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

  private async createQrCode(downloadUrl: string): Promise<void> {
    const dataUrl = await QRCode.toDataURL(this.getAbsoluteDownloadUrl(downloadUrl), {
      errorCorrectionLevel: 'M',
      margin: 2,
      width: 220,
      color: { dark: '#172b3a', light: '#ffffff' },
    });
    this.qrCodeDataUrl.set(dataUrl);
  }

  private readError(error: HttpErrorResponse, fallback: string): string {
    if (typeof error.error === 'object' && error.error && 'message' in error.error) {
      return String(error.error.message);
    }
    return fallback;
  }
}
