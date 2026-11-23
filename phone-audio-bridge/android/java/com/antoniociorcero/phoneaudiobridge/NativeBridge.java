package com.antoniociorcero.phoneaudiobridge;

public final class NativeBridge {
    static {
        System.loadLibrary("phoneaudioreceiver");
    }

    public interface Callback {
        void onNativeStateChanged(int state, String info);
        void onNativeStopped(int reason);
    }

    private NativeBridge() {}

    public static native boolean nativeStart(Callback callback, int targetMs, int sampleRate, int gainPct);
    public static native void nativeStop(int reason);
    public static native void nativeReopenRoute();
    public static native boolean nativeIsRunning();
}
