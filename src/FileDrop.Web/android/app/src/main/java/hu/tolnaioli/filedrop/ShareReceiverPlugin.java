package hu.tolnaioli.filedrop;

import android.content.ClipData;
import android.content.Intent;
import android.database.Cursor;
import android.net.Uri;
import android.provider.OpenableColumns;
import android.webkit.CookieManager;
import com.getcapacitor.JSArray;
import com.getcapacitor.JSObject;
import com.getcapacitor.Plugin;
import com.getcapacitor.PluginCall;
import com.getcapacitor.PluginMethod;
import com.getcapacitor.annotation.CapacitorPlugin;
import java.io.BufferedInputStream;
import java.io.BufferedOutputStream;
import java.io.BufferedReader;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.net.HttpURLConnection;
import java.net.URI;
import java.net.URLEncoder;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.UUID;

@CapacitorPlugin(name = "ShareReceiver")
public class ShareReceiverPlugin extends Plugin {
    private record PendingFile(String id, Uri uri, String name, long size, String type) {}

    private final Map<String, PendingFile> pendingFiles = new LinkedHashMap<>();

    @Override
    public void load() {
        captureIntent(getActivity().getIntent(), false);
    }

    @Override
    protected void handleOnNewIntent(Intent intent) {
        captureIntent(intent, true);
    }

    @PluginMethod
    public void getPendingFiles(PluginCall call) {
        call.resolve(pendingResult());
    }

    @PluginMethod
    public void clearPendingFiles(PluginCall call) {
        synchronized (pendingFiles) {
            pendingFiles.clear();
        }
        call.resolve();
    }

    @PluginMethod
    public void openExternalUrl(PluginCall call) {
        String value = call.getString("url");
        if (value == null || value.isBlank()) {
            call.reject("Hiányzik a megnyitandó cím.");
            return;
        }

        try {
            Uri uri = Uri.parse(value);
            String scheme = uri.getScheme();
            if (!"http".equalsIgnoreCase(scheme) && !"https".equalsIgnoreCase(scheme)) {
                call.reject("Csak HTTP vagy HTTPS cím nyitható meg.");
                return;
            }
            getActivity().startActivity(new Intent(Intent.ACTION_VIEW, uri));
            call.resolve();
        } catch (Exception error) {
            call.reject("A külső böngésző megnyitása nem sikerült.", error);
        }
    }

    @PluginMethod
    public void uploadFile(PluginCall call) {
        String id = call.getString("id");
        PendingFile pending;
        synchronized (pendingFiles) {
            pending = pendingFiles.get(id);
        }
        if (pending == null) {
            call.reject("A megosztott fájl már nem érhető el.");
            return;
        }

        String serverUrl = call.getString("serverUrl");
        String visibility = call.getString("visibility");
        String expiration = call.getString("expiration");
        String title = call.getString("title", "");
        String note = call.getString("note", "");
        if (serverUrl == null || visibility == null || expiration == null) {
            call.reject("Hiányos feltöltési beállítások.");
            return;
        }

        new Thread(() -> uploadInBackground(call, pending, serverUrl, visibility, expiration, title, note), "filedrop-native-upload").start();
    }

    @PluginMethod
    public void uploadBundle(PluginCall call) {
        String serverUrl = call.getString("serverUrl");
        String visibility = call.getString("visibility");
        String expiration = call.getString("expiration");
        String title = call.getString("title", "");
        String note = call.getString("note", "");
        String bundleName = call.getString("bundleName", "filedrop-csomag.zip");
        if (serverUrl == null || visibility == null || expiration == null) {
            call.reject("Hiányos feltöltési beállítások.");
            return;
        }

        List<PendingFile> files;
        synchronized (pendingFiles) {
            files = new ArrayList<>(pendingFiles.values());
        }
        if (files.size() < 2) {
            call.reject("A csomaghoz legalább két fájl szükséges.");
            return;
        }
        new Thread(() -> uploadBundleInBackground(call, files, serverUrl, visibility, expiration, title, note, bundleName), "filedrop-native-bundle-upload").start();
    }

    private void uploadInBackground(
        PluginCall call,
        PendingFile pending,
        String serverUrl,
        String visibility,
        String expiration,
        String title,
        String note
    ) {
        HttpURLConnection connection = null;
        try {
            String query = "fileName=" + encode(pending.name())
                + "&visibility=" + encode(visibility)
                + "&expiration=" + encode(expiration)
                + "&title=" + encode(title)
                + "&note=" + encode(note);
            URL endpoint = new URI(serverUrl + "/api/shares?" + query).toURL();
            connection = (HttpURLConnection) endpoint.openConnection();
            connection.setRequestMethod("POST");
            connection.setDoOutput(true);
            connection.setConnectTimeout(15_000);
            connection.setReadTimeout(120_000);
            connection.setRequestProperty("Content-Type", pending.type());
            connection.setFixedLengthStreamingMode(pending.size());

            String cookie = CookieManager.getInstance().getCookie(serverUrl);
            if (cookie != null && !cookie.isBlank()) connection.setRequestProperty("Cookie", cookie);

            long loaded = 0;
            try (
                InputStream input = new BufferedInputStream(getContext().getContentResolver().openInputStream(pending.uri()), 1024 * 1024);
                BufferedOutputStream output = new BufferedOutputStream(connection.getOutputStream(), 1024 * 1024)
            ) {
                byte[] buffer = new byte[1024 * 1024];
                int read;
                while ((read = input.read(buffer)) != -1) {
                    output.write(buffer, 0, read);
                    loaded += read;
                    JSObject progress = new JSObject();
                    progress.put("id", pending.id());
                    progress.put("loaded", loaded);
                    progress.put("total", pending.size());
                    notifyListeners("uploadProgress", progress);
                }
                output.flush();
            }

            int status = connection.getResponseCode();
            InputStream responseStream = status >= 200 && status < 300 ? connection.getInputStream() : connection.getErrorStream();
            String response = readText(responseStream);
            if (status < 200 || status >= 300) {
                call.reject(apiMessage(response, "A natív feltöltés nem sikerült (HTTP " + status + ")."));
                return;
            }

            synchronized (pendingFiles) {
                pendingFiles.remove(pending.id());
            }
            JSObject result = new JSObject();
            result.put("response", response);
            call.resolve(result);
        } catch (Exception error) {
            call.reject("A natív feltöltés megszakadt: " + error.getMessage(), error);
        } finally {
            if (connection != null) connection.disconnect();
        }
    }

    private void uploadBundleInBackground(
        PluginCall call,
        List<PendingFile> files,
        String serverUrl,
        String visibility,
        String expiration,
        String title,
        String note,
        String bundleName
    ) {
        HttpURLConnection connection = null;
        try {
            String query = "visibility=" + encode(visibility)
                + "&expiration=" + encode(expiration)
                + "&title=" + encode(title)
                + "&note=" + encode(note)
                + "&bundleName=" + encode(bundleName);
            URL endpoint = new URI(serverUrl + "/api/shares/bundle?" + query).toURL();
            String boundary = "FileDrop-" + UUID.randomUUID();
            List<byte[]> headers = new ArrayList<>();
            long contentLength = 0;
            long filesSize = 0;
            for (PendingFile file : files) {
                String fieldName = "file:" + encode(file.name()).replace("+", "%20");
                String safeName = file.name().replace("\\", "_").replace("\"", "_").replace("\r", "_").replace("\n", "_");
                String header = "--" + boundary + "\r\n"
                    + "Content-Disposition: form-data; name=\"" + fieldName + "\"; filename=\"" + safeName + "\"\r\n"
                    + "Content-Type: " + file.type() + "\r\n\r\n";
                byte[] headerBytes = header.getBytes(StandardCharsets.UTF_8);
                headers.add(headerBytes);
                contentLength += headerBytes.length + file.size() + 2;
                filesSize += file.size();
            }
            byte[] closing = ("--" + boundary + "--\r\n").getBytes(StandardCharsets.UTF_8);
            contentLength += closing.length;

            connection = (HttpURLConnection) endpoint.openConnection();
            connection.setRequestMethod("POST");
            connection.setDoOutput(true);
            connection.setConnectTimeout(15_000);
            connection.setReadTimeout(120_000);
            connection.setRequestProperty("Content-Type", "multipart/form-data; boundary=" + boundary);
            connection.setFixedLengthStreamingMode(contentLength);
            String cookie = CookieManager.getInstance().getCookie(serverUrl);
            if (cookie != null && !cookie.isBlank()) connection.setRequestProperty("Cookie", cookie);

            long loaded = 0;
            try (BufferedOutputStream output = new BufferedOutputStream(connection.getOutputStream(), 1024 * 1024)) {
                byte[] buffer = new byte[1024 * 1024];
                for (int index = 0; index < files.size(); index++) {
                    PendingFile file = files.get(index);
                    output.write(headers.get(index));
                    try (InputStream input = new BufferedInputStream(getContext().getContentResolver().openInputStream(file.uri()), 1024 * 1024)) {
                        int read;
                        while ((read = input.read(buffer)) != -1) {
                            output.write(buffer, 0, read);
                            loaded += read;
                            JSObject progress = new JSObject();
                            progress.put("id", "bundle");
                            progress.put("loaded", loaded);
                            progress.put("total", filesSize);
                            notifyListeners("uploadProgress", progress);
                        }
                    }
                    output.write("\r\n".getBytes(StandardCharsets.UTF_8));
                }
                output.write(closing);
                output.flush();
            }

            int status = connection.getResponseCode();
            InputStream responseStream = status >= 200 && status < 300 ? connection.getInputStream() : connection.getErrorStream();
            String response = readText(responseStream);
            if (status < 200 || status >= 300) {
                call.reject(apiMessage(response, "A natív ZIP-feltöltés nem sikerült (HTTP " + status + ")."));
                return;
            }
            synchronized (pendingFiles) {
                for (PendingFile file : files) pendingFiles.remove(file.id());
            }
            JSObject result = new JSObject();
            result.put("response", response);
            call.resolve(result);
        } catch (Exception error) {
            call.reject("A natív ZIP-feltöltés megszakadt: " + error.getMessage(), error);
        } finally {
            if (connection != null) connection.disconnect();
        }
    }

    private void captureIntent(Intent intent, boolean notify) {
        if (intent == null || (!Intent.ACTION_SEND.equals(intent.getAction()) && !Intent.ACTION_SEND_MULTIPLE.equals(intent.getAction()))) return;

        List<Uri> uris = extractUris(intent);
        synchronized (pendingFiles) {
            pendingFiles.clear();
            for (Uri uri : uris) {
                PendingFile file = describe(uri, intent.getType());
                if (file != null) pendingFiles.put(file.id(), file);
            }
        }
        if (notify) notifyListeners("shareReceived", pendingResult(), true);
        intent.setAction(null);
    }

    @SuppressWarnings("deprecation")
    private List<Uri> extractUris(Intent intent) {
        List<Uri> result = new ArrayList<>();
        ClipData clipData = intent.getClipData();
        if (clipData != null) {
            for (int index = 0; index < clipData.getItemCount(); index++) {
                Uri uri = clipData.getItemAt(index).getUri();
                if (uri != null) result.add(uri);
            }
        } else if (Intent.ACTION_SEND_MULTIPLE.equals(intent.getAction())) {
            ArrayList<Uri> values = intent.getParcelableArrayListExtra(Intent.EXTRA_STREAM);
            if (values != null) result.addAll(values);
        } else {
            Uri uri = intent.getParcelableExtra(Intent.EXTRA_STREAM);
            if (uri != null) result.add(uri);
        }
        return result;
    }

    private PendingFile describe(Uri uri, String fallbackType) {
        String name = "megosztott-fajl";
        long size = -1;
        try (Cursor cursor = getContext().getContentResolver().query(uri, null, null, null, null)) {
            if (cursor != null && cursor.moveToFirst()) {
                int nameIndex = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME);
                int sizeIndex = cursor.getColumnIndex(OpenableColumns.SIZE);
                if (nameIndex >= 0 && !cursor.isNull(nameIndex)) name = cursor.getString(nameIndex);
                if (sizeIndex >= 0 && !cursor.isNull(sizeIndex)) size = cursor.getLong(sizeIndex);
            }
        }
        if (size <= 0) return null;
        String type = getContext().getContentResolver().getType(uri);
        if (type == null || type.isBlank()) type = fallbackType;
        if (type == null || type.isBlank()) type = "application/octet-stream";
        return new PendingFile(UUID.randomUUID().toString(), uri, name, size, type);
    }

    private JSObject pendingResult() {
        JSArray files = new JSArray();
        synchronized (pendingFiles) {
            for (PendingFile file : pendingFiles.values()) {
                JSObject item = new JSObject();
                item.put("id", file.id());
                item.put("name", file.name());
                item.put("size", file.size());
                item.put("type", file.type());
                files.put(item);
            }
        }
        JSObject result = new JSObject();
        result.put("files", files);
        return result;
    }

    private static String encode(String value) {
        return URLEncoder.encode(value == null ? "" : value, StandardCharsets.UTF_8);
    }

    private static String readText(InputStream stream) throws Exception {
        if (stream == null) return "";
        StringBuilder result = new StringBuilder();
        try (BufferedReader reader = new BufferedReader(new InputStreamReader(stream, StandardCharsets.UTF_8))) {
            String line;
            while ((line = reader.readLine()) != null) result.append(line);
        }
        return result.toString();
    }

    private static String apiMessage(String response, String fallback) {
        if (response == null || response.isBlank()) return fallback;
        try {
            JSObject json = new JSObject(response);
            String message = json.getString("message");
            return message == null || message.isBlank() ? fallback : message;
        } catch (Exception ignored) {
            return fallback;
        }
    }
}
