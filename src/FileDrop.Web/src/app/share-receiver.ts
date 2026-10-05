import { PluginListenerHandle, registerPlugin } from '@capacitor/core';

export interface NativeSharedFile {
  id: string;
  name: string;
  size: number;
  type: string;
}

interface PendingFilesResult {
  files: NativeSharedFile[];
}

export interface NativeUploadOptions {
  id: string;
  serverUrl: string;
  visibility: string;
  expiration: string;
  title: string;
  note: string;
}

interface NativeUploadResult {
  response: string;
}

export interface NativeUploadProgress {
  id: string;
  loaded: number;
  total: number;
}

interface NativeExternalUrlOptions {
  url: string;
}

interface ShareReceiverPlugin {
  getPendingFiles(): Promise<PendingFilesResult>;
  clearPendingFiles(): Promise<void>;
  uploadFile(options: NativeUploadOptions): Promise<NativeUploadResult>;
  uploadBundle(options: Omit<NativeUploadOptions, 'id'> & { bundleName: string }): Promise<NativeUploadResult>;
  openExternalUrl(options: NativeExternalUrlOptions): Promise<void>;
  addListener(eventName: 'shareReceived', listener: (result: PendingFilesResult) => void): Promise<PluginListenerHandle>;
  addListener(eventName: 'uploadProgress', listener: (progress: NativeUploadProgress) => void): Promise<PluginListenerHandle>;
}

export const ShareReceiver = registerPlugin<ShareReceiverPlugin>('ShareReceiver');
