package io.github.ajtn123.yokowave.sdk;

/** Protocol error code and key, without the request, credentials or remote stack trace. */
public final class RpcException extends Exception {
    private final String code;
    public RpcException(String code) { super("Remote request rejected: " + code); this.code = code; }
    public String code() { return code; }
}
