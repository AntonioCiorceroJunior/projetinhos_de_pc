package com.antoniociorcero.phoneaudiobridge;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.media.AudioDeviceCallback;
import android.media.AudioDeviceInfo;
import android.media.AudioManager;
import android.os.Build;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.os.PowerManager;

public final class AudioBridgeService extends Service implements NativeBridge.Callback {
    public static final String ACTION_START = "com.antoniociorcero.phoneaudiobridge.START";
    public static final String ACTION_USER_STOP = "com.antoniociorcero.phoneaudiobridge.USER_STOP";
    public static final String EXTRA_TARGET_MS = "target_ms";
    public static final String EXTRA_SAMPLE_RATE = "sample_rate";
    public static final String EXTRA_GAIN_PCT = "gain_pct";

    public static final String BROADCAST_STATE = "com.antoniociorcero.phoneaudiobridge.STATE_CHANGED";
    public static final String EXTRA_STATE_TEXT = "state_text";
    public static final String EXTRA_RUNNING = "running";

    private static final String CHANNEL_ID = "phone_audio_bridge_usb";
    private static final int NOTIF_ID = 1001;

    private static volatile boolean sRunning = false;
    private static volatile String sStatusText = "Aguardando conexão USB...";

    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private AudioManager audioManager;
    private PowerManager.WakeLock wakeLock;
    private AudioDeviceCallback audioDeviceCallback;
    private boolean receiversRegistered = false;
    private volatile boolean stopping = false;
    private String lastNativeInfo = "";

    private final Runnable reopenRouteRunnable = new Runnable() {
        @Override
        public void run() {
            if (!sRunning || stopping) return;
            NativeBridge.nativeReopenRoute();
            refreshRouteUiLabel();
        }
    };

    public static boolean isServiceRunning() {
        return sRunning;
    }

    public static String getCurrentStatusText() {
        return sStatusText;
    }

    @Override
    public void onCreate() {
        super.onCreate();
        audioManager = (AudioManager) getSystemService(Context.AUDIO_SERVICE);
        PowerManager pm = (PowerManager) getSystemService(Context.POWER_SERVICE);
        if (pm != null) {
            wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "PhoneAudioBridge:UsbAudioLock");
            wakeLock.setReferenceCounted(false);
        }
        createNotificationChannel();
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        String action = intent != null ? intent.getAction() : ACTION_START;
        if (ACTION_USER_STOP.equals(action)) {
            stopSessionAndService(2, "Desconectado pelo celular (não reconecta sozinho).");
            return START_NOT_STICKY;
        }

        int targetMs = intent != null ? intent.getIntExtra(EXTRA_TARGET_MS, 10) : 10;
        int sampleRate = intent != null ? intent.getIntExtra(EXTRA_SAMPLE_RATE, 48000) : 48000;
        int gainPct = intent != null ? intent.getIntExtra(EXTRA_GAIN_PCT, 100) : 100;

        stopping = false;
        sRunning = true;
        publishState("Iniciando canal USB nativo (AAudio)...", true);
        startAsForeground("Aguardando fluxo de áudio do PC via USB...");
        registerDisconnectMonitors();

        if (wakeLock != null && !wakeLock.isHeld()) {
            try {
                wakeLock.acquire();
            } catch (Throwable ignored) {}
        }

        boolean ok = NativeBridge.nativeStart(this, targetMs, sampleRate, gainPct);
        if (!ok) {
            stopSessionAndService(3, "Falha ao abrir socket USB/AAudio nativo.");
        }
        return START_NOT_STICKY;
    }

    private void createNotificationChannel() {
        NotificationManager nm = (NotificationManager) getSystemService(Context.NOTIFICATION_SERVICE);
        if (nm != null && Build.VERSION.SDK_INT >= 26) {
            NotificationChannel ch = new NotificationChannel(
                CHANNEL_ID,
                "Phone Audio Bridge USB",
                NotificationManager.IMPORTANCE_LOW
            );
            ch.setShowBadge(false);
            nm.createNotificationChannel(ch);
        }
    }

    private Notification buildNotification(String text) {
        Intent openIntent = new Intent(this, BridgeActivity.class);
        openIntent.setFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP | Intent.FLAG_ACTIVITY_CLEAR_TOP);
        PendingIntent contentPi = PendingIntent.getActivity(
            this, 0, openIntent, PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE
        );

        Intent stopIntent = new Intent(this, AudioBridgeService.class);
        stopIntent.setAction(ACTION_USER_STOP);
        PendingIntent stopPi = PendingIntent.getService(
            this, 1, stopIntent, PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE
        );

        Notification.Builder b = new Notification.Builder(this, CHANNEL_ID)
            .setContentTitle("Phone Audio Bridge (USB Nativo)")
            .setContentText(text)
            .setSmallIcon(android.R.drawable.stat_sys_headset)
            .setOngoing(true)
            .setContentIntent(contentPi)
            .addAction(new Notification.Action.Builder(
                android.graphics.drawable.Icon.createWithResource(this, android.R.drawable.ic_menu_close_clear_cancel),
                "Desconectar",
                stopPi).build());

        return b.build();
    }

    private void startAsForeground(String text) {
        Notification n = buildNotification(text);
        if (Build.VERSION.SDK_INT >= 29) {
            startForeground(NOTIF_ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK);
        } else {
            startForeground(NOTIF_ID, n);
        }
    }

    private void updateNotification(String text) {
        NotificationManager nm = (NotificationManager) getSystemService(Context.NOTIFICATION_SERVICE);
        if (nm != null && sRunning && !stopping) {
            nm.notify(NOTIF_ID, buildNotification(text));
        }
    }

    private static boolean isHeadsetDeviceType(int t) {
        return t == AudioDeviceInfo.TYPE_BLUETOOTH_A2DP ||
               t == AudioDeviceInfo.TYPE_BLE_HEADSET ||
               t == AudioDeviceInfo.TYPE_BLE_SPEAKER ||
               t == AudioDeviceInfo.TYPE_WIRED_HEADSET ||
               t == AudioDeviceInfo.TYPE_WIRED_HEADPHONES ||
               t == AudioDeviceInfo.TYPE_USB_HEADSET;
    }

    private String describeCurrentOutput() {
        if (audioManager != null) {
            try {
                AudioDeviceInfo[] outs = audioManager.getDevices(AudioManager.GET_DEVICES_OUTPUTS);
                if (outs != null) {
                    for (AudioDeviceInfo dev : outs) {
                        if (isHeadsetDeviceType(dev.getType())) {
                            return "Fone conectado";
                        }
                    }
                }
            } catch (Throwable ignored) {}
        }
        return "Alto-falante do celular";
    }

    private void refreshRouteUiLabel() {
        if (!sRunning || stopping) return;
        String suffix = lastNativeInfo != null && lastNativeInfo.length() > 0 ? " (" + lastNativeInfo + ")" : "";
        String txt = "Saída: " + describeCurrentOutput() + suffix;
        publishState(txt, true);
        updateNotification(txt);
    }

    private void scheduleRouteReopen(AudioDeviceInfo[] devices) {
        if (devices == null || !sRunning || stopping) return;
        for (AudioDeviceInfo dev : devices) {
            if (!dev.isSink()) continue;
            if (isHeadsetDeviceType(dev.getType())) {
                mainHandler.removeCallbacks(reopenRouteRunnable);
                mainHandler.postDelayed(reopenRouteRunnable, 160);
                return;
            }
        }
    }

    private void registerDisconnectMonitors() {
        if (receiversRegistered) return;
        receiversRegistered = true;

        if (audioManager != null) {
            audioDeviceCallback = new AudioDeviceCallback() {
                @Override
                public void onAudioDevicesAdded(AudioDeviceInfo[] addedDevices) {
                    scheduleRouteReopen(addedDevices);
                }

                @Override
                public void onAudioDevicesRemoved(AudioDeviceInfo[] removedDevices) {
                    scheduleRouteReopen(removedDevices);
                }
            };
            audioManager.registerAudioDeviceCallback(audioDeviceCallback, mainHandler);
        }
    }

    private void unregisterDisconnectMonitors() {
        mainHandler.removeCallbacks(reopenRouteRunnable);
        if (!receiversRegistered) return;
        receiversRegistered = false;
        if (audioManager != null && audioDeviceCallback != null) {
            try { audioManager.unregisterAudioDeviceCallback(audioDeviceCallback); } catch (Throwable ignored) {}
            audioDeviceCallback = null;
        }
    }

    private void stopSessionAndService(int reason, String statusMsg) {
        if (stopping) return;
        stopping = true;
        sRunning = false;
        unregisterDisconnectMonitors();
        NativeBridge.nativeStop(reason);
        if (wakeLock != null && wakeLock.isHeld()) {
            try { wakeLock.release(); } catch (Throwable ignored) {}
        }
        publishState(statusMsg, false);
        try {
            stopForeground(STOP_FOREGROUND_REMOVE);
        } catch (Throwable ignored) {}
        stopSelf();
    }

    private void publishState(String text, boolean running) {
        sStatusText = text;
        Intent i = new Intent(BROADCAST_STATE);
        i.setPackage(getPackageName());
        i.putExtra(EXTRA_STATE_TEXT, text);
        i.putExtra(EXTRA_RUNNING, running);
        sendBroadcast(i);
    }

    @Override
    public void onNativeStateChanged(final int state, final String info) {
        mainHandler.post(new Runnable() {
            @Override
            public void run() {
                if (stopping) return;
                lastNativeInfo = info != null ? info : "";
                String route = describeCurrentOutput();
                if (state == 2) {
                    String txt = "Transmitindo · " + route + " (" + lastNativeInfo + ")";
                    publishState(txt, true);
                    updateNotification(txt);
                } else if (state == 1) {
                    String txt = "Canal USB pronto · " + route + " (" + lastNativeInfo + ")";
                    publishState(txt, true);
                    updateNotification(txt);
                }
            }
        });
    }

    @Override
    public void onNativeStopped(final int reason) {
        mainHandler.post(new Runnable() {
            @Override
            public void run() {
                String msg;
                if (reason == 1) {
                    msg = "Desconectado (Bluetooth/fone removido — sem reconexão automática).";
                } else if (reason == 2) {
                    msg = "Desconectado pelo usuário.";
                } else if (reason == 0) {
                    msg = "Sessão encerrada pelo PC.";
                } else {
                    msg = "Conexão USB encerrada.";
                }
                stopSessionAndService(reason, msg);
            }
        });
    }

    @Override
    public void onDestroy() {
        stopping = true;
        sRunning = false;
        unregisterDisconnectMonitors();
        NativeBridge.nativeStop(0);
        if (wakeLock != null && wakeLock.isHeld()) {
            try { wakeLock.release(); } catch (Throwable ignored) {}
        }
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }
}
