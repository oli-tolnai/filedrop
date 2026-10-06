package hu.tolnaioli.filedrop;

import android.os.Bundle;
import com.getcapacitor.CapConfig;
import com.getcapacitor.BridgeActivity;
import java.net.HttpURLConnection;
import java.net.URI;
import java.net.URL;

public class MainActivity extends BridgeActivity {
    private static final String LAN_SERVER_URL = "http://192.168.0.34:8090";
    private static final String TAILSCALE_SERVER_URL = "https://filedrop-tailscale.tailbbb228.ts.net";

    @Override
    public void onCreate(Bundle savedInstanceState) {
        registerPlugin(ShareReceiverPlugin.class);
        super.onCreate(savedInstanceState);
    }

    @Override
    protected void load() {
        // A hálózati próba háttérszálon fut, de a Bridge létrehozása még a
        // BridgeActivity.load() hívásán belül történik. Így az Activity nem
        // marad félig inicializált állapotban Android-induláskor.
        final String[] selectedUrl = {LAN_SERVER_URL};
        Thread probe = new Thread(() -> selectedUrl[0] = resolveServerUrl(), "filedrop-server-selection");
        probe.start();
        try {
            probe.join(2500L);
        } catch (InterruptedException interrupted) {
            Thread.currentThread().interrupt();
        }
        startBridge(selectedUrl[0]);
    }

    private void startBridge(String serverUrl) {
        CapConfig config = new CapConfig.Builder(this)
            .setServerUrl(serverUrl)
            .setAllowNavigation(new String[] {
                "192.168.0.34",
                "filedrop-tailscale.tailbbb228.ts.net"
            })
            .create();

        bridge = bridgeBuilder.addPlugins(initialPlugins).setConfig(config).create();
        keepRunning = bridge.shouldKeepRunning();
        onNewIntent(getIntent());
    }

    private static String resolveServerUrl() {
        if (isHealthy(LAN_SERVER_URL)) return LAN_SERVER_URL;
        if (isHealthy(TAILSCALE_SERVER_URL)) return TAILSCALE_SERVER_URL;
        // Hibaoldalon is a LAN-címet próbálunk, hogy a felhasználó egyértelmű
        // kapcsolat- vagy Tailscale-állapotot lásson, ne üres alkalmazást.
        return LAN_SERVER_URL;
    }

    private static boolean isHealthy(String serverUrl) {
        HttpURLConnection connection = null;
        try {
            URL url = new URI(serverUrl + "/api/health").toURL();
            connection = (HttpURLConnection) url.openConnection();
            connection.setConnectTimeout(800);
            connection.setReadTimeout(800);
            connection.setRequestMethod("GET");
            connection.setUseCaches(false);
            return connection.getResponseCode() == HttpURLConnection.HTTP_OK;
        } catch (Exception ignored) {
            return false;
        } finally {
            if (connection != null) connection.disconnect();
        }
    }
}
