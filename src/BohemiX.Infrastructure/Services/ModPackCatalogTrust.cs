namespace BohemiX.Infrastructure.Services;

public static class ModPackCatalogTrust
{
    // Deployment replaces this review key only through a source-controlled trust-key rotation.
    public const string DefaultPublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEaI0azecSgDw3CvKJlp2G6UXcAeRZ
        BpXtqgeY/biixuGxP/cCOYf1zjG7kWNNVYurSLTk7WMIclNdPRN5BIhW8g==
        -----END PUBLIC KEY-----
        """;
}
