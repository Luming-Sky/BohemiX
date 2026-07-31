using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BohemiX.Core.Models;
using BohemiX.Infrastructure.Services;

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    Converters = { new JsonStringEnumConverter() }
};

if (args.Length < 2)
{
    return ShowUsage();
}

try
{
    return args[0].ToLowerInvariant() switch
    {
        "validate" => ValidateCatalog(args[1]),
        "sign" when args.Length is 3 or 4 => SignCatalog(args[1], args[2], args.Length == 4 ? args[3] : args[1] + ".sig"),
        "generate-key" when args.Length == 3 => GenerateKey(args[1], args[2]),
        _ => ShowUsage()
    };
}
catch (Exception ex) when (ex is IOException
                           or UnauthorizedAccessException
                           or JsonException
                           or CryptographicException
                           or InvalidDataException
                           or ArgumentException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

int ValidateCatalog(string catalogPath)
{
    var document = JsonSerializer.Deserialize<ModPackCatalogDocument>(File.ReadAllBytes(catalogPath), jsonOptions);
    var errors = ModPackCatalogValidator.Validate(document);
    if (errors.Count == 0)
    {
        Console.WriteLine("Catalog is valid.");
        return 0;
    }

    foreach (var error in errors)
    {
        Console.Error.WriteLine(error);
    }

    return 1;
}

int SignCatalog(string catalogPath, string privateKeyPath, string signaturePath)
{
    EnsurePrivateKeyOutsideRepository(privateKeyPath);
    if (ValidateCatalog(catalogPath) != 0)
    {
        return 1;
    }

    var catalogBytes = File.ReadAllBytes(catalogPath);
    using var key = ECDsa.Create();
    key.ImportFromPem(File.ReadAllText(privateKeyPath));
    var signature = key.SignData(catalogBytes, HashAlgorithmName.SHA256);
    File.WriteAllText(signaturePath, Convert.ToBase64String(signature) + Environment.NewLine, Encoding.ASCII);
    Console.WriteLine($"Wrote signature: {Path.GetFullPath(signaturePath)}");
    return 0;
}

int GenerateKey(string privateKeyPath, string publicKeyPath)
{
    EnsurePrivateKeyOutsideRepository(privateKeyPath);
    if (File.Exists(privateKeyPath) || File.Exists(publicKeyPath))
    {
        throw new IOException("Refusing to overwrite an existing key file.");
    }

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(privateKeyPath))!);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(publicKeyPath))!);
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    File.WriteAllText(privateKeyPath, key.ExportECPrivateKeyPem(), Encoding.ASCII);
    File.WriteAllText(publicKeyPath, key.ExportSubjectPublicKeyInfoPem(), Encoding.ASCII);
    Console.WriteLine($"Wrote private key outside the repository: {Path.GetFullPath(privateKeyPath)}");
    Console.WriteLine($"Wrote public key: {Path.GetFullPath(publicKeyPath)}");
    return 0;
}

void EnsurePrivateKeyOutsideRepository(string privateKeyPath)
{
    var privatePath = Path.GetFullPath(privateKeyPath);
    var privateDirectory = Path.GetDirectoryName(privatePath)
        ?? throw new InvalidDataException("Private-key path has no parent directory.");
    if (FindRepositoryRoot(privateDirectory) is not null)
    {
        throw new InvalidDataException("Private keys must be generated outside the repository.");
    }
}

string? FindRepositoryRoot(string startPath)
{
    for (var directory = new DirectoryInfo(startPath); directory is not null; directory = directory.Parent)
    {
        if (Directory.Exists(Path.Combine(directory.FullName, ".git")))
        {
            return directory.FullName;
        }
    }

    return null;
}

int ShowUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  BohemiX.ModPackCatalogTool validate <catalog.json>");
    Console.Error.WriteLine("  BohemiX.ModPackCatalogTool sign <catalog.json> <private-key.pem> [catalog.json.sig]");
    Console.Error.WriteLine("  BohemiX.ModPackCatalogTool generate-key <private-key.pem> <public-key.pem>");
    return 2;
}
