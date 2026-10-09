using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;
using MessagePack;
using Nethereum.Signer;
using Nethereum.Signer.EIP712;
using Nethereum.Util;

namespace TradeOps.Infrastructure.Exchange.Hyperliquid;

internal sealed record HyperliquidSignature(
    string R,
    string S,
    int V);

internal static class HyperliquidL1Signer
{
    private const string ZeroAddress =
        "0x0000000000000000000000000000000000000000";

    public static HyperliquidSignature SignOrderTestnet(
        string privateKey,
        long nonce,
        int asset,
        bool isBuy,
        string price,
        string size,
        bool reduceOnly,
        string tif,
        string? cloid = null)
    {
        var action = EncodeOrderAction(
            asset,
            isBuy,
            price,
            size,
            reduceOnly,
            tif,
            cloid);

        return SignTestnet(privateKey, action, nonce);
    }

    public static HyperliquidSignature SignCancelTestnet(
        string privateKey,
        long nonce,
        int asset,
        long orderId)
    {
        var action = EncodeCancelAction(asset, orderId);
        return SignTestnet(privateKey, action, nonce);
    }

    public static HyperliquidSignature SignCancelByCloidTestnet(
        string privateKey,
        long nonce,
        int asset,
        string cloid)
    {
        var action = EncodeCancelByCloidAction(asset, cloid);
        return SignTestnet(privateKey, action, nonce);
    }

    internal static byte[] CalculateOrderActionHash(
        long nonce,
        int asset,
        bool isBuy,
        string price,
        string size,
        bool reduceOnly,
        string tif,
        string? cloid = null)
    {
        var action = EncodeOrderAction(
            asset,
            isBuy,
            price,
            size,
            reduceOnly,
            tif,
            cloid);

        return CalculateActionHash(action, nonce);
    }

    private static HyperliquidSignature SignTestnet(
        string privateKey,
        byte[] encodedAction,
        long nonce)
    {
        ValidatePrivateKey(privateKey);

        var actionHash = CalculateActionHash(encodedAction, nonce);
        var typedDataJson = JsonSerializer.Serialize(new
        {
            domain = new
            {
                chainId = 1337,
                name = "Exchange",
                verifyingContract = ZeroAddress,
                version = "1"
            },
            types = new Dictionary<string, object>
            {
                ["Agent"] = new object[]
                {
                    new { name = "source", type = "string" },
                    new { name = "connectionId", type = "bytes32" }
                },
                ["EIP712Domain"] = new object[]
                {
                    new { name = "name", type = "string" },
                    new { name = "version", type = "string" },
                    new { name = "chainId", type = "uint256" },
                    new { name = "verifyingContract", type = "address" }
                }
            },
            primaryType = "Agent",
            message = new
            {
                source = "b",
                connectionId = $"0x{Convert.ToHexString(actionHash).ToLowerInvariant()}"
            }
        });

        var key = new EthECKey(NormalizePrivateKey(privateKey));
        var signatureHex =
            new Eip712TypedDataSigner().SignTypedDataV4(
                typedDataJson,
                key);

        return ParseSignature(signatureHex);
    }

    private static byte[] CalculateActionHash(
        byte[] encodedAction,
        long nonce)
    {
        var payload = new byte[encodedAction.Length + 9];
        Buffer.BlockCopy(
            encodedAction,
            0,
            payload,
            0,
            encodedAction.Length);

        BinaryPrimitives.WriteInt64BigEndian(
            payload.AsSpan(encodedAction.Length, 8),
            nonce);

        // activePool/vaultAddress is null.
        payload[^1] = 0;

        return Sha3Keccack.Current.CalculateHash(payload);
    }

    private static byte[] EncodeOrderAction(
        int asset,
        bool isBuy,
        string price,
        string size,
        bool reduceOnly,
        string tif,
        string? cloid)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);

        writer.WriteMapHeader(3);

        writer.Write("type");
        writer.Write("order");

        writer.Write("orders");
        writer.WriteArrayHeader(1);

        writer.WriteMapHeader(cloid is null ? 6 : 7);
        writer.Write("a");
        writer.Write(asset);
        writer.Write("b");
        writer.Write(isBuy);
        writer.Write("p");
        writer.Write(price);
        writer.Write("s");
        writer.Write(size);
        writer.Write("r");
        writer.Write(reduceOnly);
        writer.Write("t");
        writer.WriteMapHeader(1);
        writer.Write("limit");
        writer.WriteMapHeader(1);
        writer.Write("tif");
        writer.Write(tif);

        if (cloid is not null)
        {
            writer.Write("c");
            writer.Write(cloid);
        }

        writer.Write("grouping");
        writer.Write("na");

        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] EncodeCancelAction(
        int asset,
        long orderId)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);

        writer.WriteMapHeader(2);
        writer.Write("type");
        writer.Write("cancel");
        writer.Write("cancels");
        writer.WriteArrayHeader(1);
        writer.WriteMapHeader(2);
        writer.Write("a");
        writer.Write(asset);
        writer.Write("o");
        writer.Write(orderId);

        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] EncodeCancelByCloidAction(
        int asset,
        string cloid)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);

        writer.WriteMapHeader(2);
        writer.Write("type");
        writer.Write("cancelByCloid");
        writer.Write("cancels");
        writer.WriteArrayHeader(1);
        writer.WriteMapHeader(2);
        writer.Write("asset");
        writer.Write(asset);
        writer.Write("cloid");
        writer.Write(cloid);

        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static HyperliquidSignature ParseSignature(
        string signatureHex)
    {
        var hex = signatureHex.StartsWith(
            "0x",
            StringComparison.OrdinalIgnoreCase)
            ? signatureHex[2..]
            : signatureHex;

        if (hex.Length != 130)
        {
            throw new InvalidOperationException(
                "Unexpected Hyperliquid EIP-712 signature length.");
        }

        var r = $"0x{hex[..64].ToLowerInvariant()}";
        var s = $"0x{hex.Substring(64, 64).ToLowerInvariant()}";
        var rawV = Convert.ToInt32(hex.Substring(128, 2), 16);
        var v = rawV < 27 ? rawV + 27 : rawV;

        return new HyperliquidSignature(r, s, v);
    }

    private static void ValidatePrivateKey(string privateKey)
    {
        var normalized = NormalizePrivateKey(privateKey);

        if (normalized.Length != 64
            || !normalized.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException(
                "Hyperliquid testnet PrivateKey must be a 32-byte hexadecimal secp256k1 private key.");
        }
    }

    private static string NormalizePrivateKey(string privateKey) =>
        privateKey.Trim().StartsWith(
            "0x",
            StringComparison.OrdinalIgnoreCase)
            ? privateKey.Trim()[2..]
            : privateKey.Trim();
}
