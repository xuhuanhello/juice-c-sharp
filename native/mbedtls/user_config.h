/**
 * datachannel-unity — Mbed TLS user config (via MBEDTLS_USER_CONFIG_FILE).
 * Required by libdatachannel v0.24.5 DtlsTransport when USE_MBEDTLS=ON.
 */
#pragma once

#ifndef MBEDTLS_SSL_DTLS_SRTP
#define MBEDTLS_SSL_DTLS_SRTP
#endif

#ifndef MBEDTLS_SSL_PROTO_DTLS
#define MBEDTLS_SSL_PROTO_DTLS
#endif
