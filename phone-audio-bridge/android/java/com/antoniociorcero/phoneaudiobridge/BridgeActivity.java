package com.antoniociorcero.phoneaudiobridge;

import android.app.Activity;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.graphics.Color;
import android.graphics.Typeface;
import android.os.Build;
import android.os.Bundle;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.TextView;

public final class BridgeActivity extends Activity {
    private TextView statusView;
    private Button disconnectBtn;
    private final BroadcastReceiver stateReceiver = new BroadcastReceiver() {
        @Override
        public void onReceive(Context context, Intent intent) {
            if (intent == null) return;
            String text = intent.getStringExtra(AudioBridgeService.EXTRA_STATE_TEXT);
            boolean running = intent.getBooleanExtra(AudioBridgeService.EXTRA_RUNNING, true);
            if (statusView != null && text != null) {
                statusView.setText(text);
            }
            if (!running && !isFinishing()) {
                finishAndRemoveTask();
            }
        }
    };

    private int dp(int v) {
        return (int) TypedValue.applyDimension(
            TypedValue.COMPLEX_UNIT_DIP, v, getResources().getDisplayMetrics()
        );
    }

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setGravity(Gravity.CENTER_VERTICAL);
        root.setBackgroundColor(Color.rgb(15, 19, 26));
        root.setPadding(dp(24), dp(32), dp(24), dp(32));

        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setBackgroundColor(Color.rgb(25, 31, 43));
        card.setPadding(dp(20), dp(22), dp(20), dp(22));

        TextView title = new TextView(this);
        title.setText("Phone Audio Bridge · USB Nativo");
        title.setTextColor(Color.rgb(241, 245, 249));
        title.setTextSize(TypedValue.COMPLEX_UNIT_SP, 20);
        title.setTypeface(Typeface.DEFAULT_BOLD);
        card.addView(title);

        TextView badge = new TextView(this);
        badge.setText("MOTOR NATIVO C++ AAUDIO · BAIXA LATÊNCIA");
        badge.setTextColor(Color.rgb(74, 222, 128));
        badge.setTextSize(TypedValue.COMPLEX_UNIT_SP, 11);
        badge.setTypeface(Typeface.DEFAULT_BOLD);
        badge.setPadding(0, dp(6), 0, dp(14));
        card.addView(badge);

        statusView = new TextView(this);
        statusView.setText(AudioBridgeService.getCurrentStatusText());
        statusView.setTextColor(Color.rgb(56, 189, 248));
        statusView.setTextSize(TypedValue.COMPLEX_UNIT_SP, 15);
        statusView.setPadding(0, dp(4), 0, dp(16));
        card.addView(statusView);

        TextView tip = new TextView(this);
        tip.setText("Troca automática: com fone conectado sai no fone; sem fone (ou ao desligar o Bluetooth) continua no alto-falante do celular. Só desconecta quando você tocar em Desconectar.");
        tip.setTextColor(Color.rgb(148, 163, 184));
        tip.setTextSize(TypedValue.COMPLEX_UNIT_SP, 13);
        tip.setPadding(0, 0, 0, dp(20));
        card.addView(tip);

        disconnectBtn = new Button(this);
        disconnectBtn.setText("Desconectar");
        disconnectBtn.setTextColor(Color.WHITE);
        disconnectBtn.setBackgroundColor(Color.rgb(220, 38, 38));
        disconnectBtn.setTextSize(TypedValue.COMPLEX_UNIT_SP, 16);
        disconnectBtn.setTypeface(Typeface.DEFAULT_BOLD);
        disconnectBtn.setPadding(dp(16), dp(14), dp(16), dp(14));
        disconnectBtn.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                Intent stop = new Intent(BridgeActivity.this, AudioBridgeService.class);
                stop.setAction(AudioBridgeService.ACTION_USER_STOP);
                startService(stop);
                finishAndRemoveTask();
            }
        });
        card.addView(disconnectBtn);

        root.addView(card);
        setContentView(root);

        handleLaunchIntent(getIntent());
    }

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        handleLaunchIntent(intent);
    }

    private void handleLaunchIntent(Intent intent) {
        if (intent != null && intent.getBooleanExtra("stop_now", false)) {
            Intent stop = new Intent(this, AudioBridgeService.class);
            stop.setAction(AudioBridgeService.ACTION_USER_STOP);
            startService(stop);
            finishAndRemoveTask();
            return;
        }

        int targetMs = intent != null ? intent.getIntExtra(AudioBridgeService.EXTRA_TARGET_MS, 10) : 10;
        int sampleRate = intent != null ? intent.getIntExtra(AudioBridgeService.EXTRA_SAMPLE_RATE, 48000) : 48000;
        int gainPct = intent != null ? intent.getIntExtra(AudioBridgeService.EXTRA_GAIN_PCT, 100) : 100;

        Intent svc = new Intent(this, AudioBridgeService.class);
        svc.setAction(AudioBridgeService.ACTION_START);
        svc.putExtra(AudioBridgeService.EXTRA_TARGET_MS, targetMs);
        svc.putExtra(AudioBridgeService.EXTRA_SAMPLE_RATE, sampleRate);
        svc.putExtra(AudioBridgeService.EXTRA_GAIN_PCT, gainPct);

        if (Build.VERSION.SDK_INT >= 26) {
            startForegroundService(svc);
        } else {
            startService(svc);
        }
    }

    @Override
    protected void onStart() {
        super.onStart();
        IntentFilter filter = new IntentFilter(AudioBridgeService.BROADCAST_STATE);
        if (Build.VERSION.SDK_INT >= 33) {
            registerReceiver(stateReceiver, filter, Context.RECEIVER_NOT_EXPORTED);
        } else {
            registerReceiver(stateReceiver, filter);
        }
        if (statusView != null) {
            statusView.setText(AudioBridgeService.getCurrentStatusText());
        }
    }

    @Override
    protected void onStop() {
        try {
            unregisterReceiver(stateReceiver);
        } catch (Throwable ignored) {}
        super.onStop();
    }
}
