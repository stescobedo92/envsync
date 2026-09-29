using System.Buffers;
using System.Text;
using System.Text.Json;
using EnvSync.Domain;

namespace EnvSync.Application.Secrets;

/// <summary>
/// Picks the value a <see cref="SecretReference"/> points at from a secret's raw text. Without a field the text
/// is the value; with a field the text must be a JSON object and the field a top-level scalar property.
/// </summary>
public static class StructuredSecret
{
    private const string NotAnObject = "The secret is not a valid JSON object, so a #field cannot be selected from it.";

    public static Result<SecretValue> Select(string payload, SecretReference reference)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (reference.Field is not { } field)
        {
            return Result<SecretValue>.Success(new SecretValue(payload));
        }

        // The payload may hold several secrets; the transcoding buffer is wiped before it goes back to the pool.
        var rented = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(payload.Length));
        try
        {
            var length = Encoding.UTF8.GetBytes(payload, rented);
            return Find(rented.AsSpan(0, length), reference, field);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    private static Result<SecretValue> Find(ReadOnlySpan<byte> utf8, SecretReference reference, string field)
    {
        var reader = new Utf8JsonReader(utf8);

        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return Fail(reference, ErrorKind.FieldNotFound, NotAnObject);
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isTheField = reader.ValueTextEquals(field);
                reader.Read();

                if (isTheField)
                {
                    return ToScalar(ref reader, reference, field);
                }

                reader.Skip();
            }

            return Fail(reference, ErrorKind.FieldNotFound, $"The secret has no field '{field}'.");
        }
        catch (JsonException)
        {
            return Fail(reference, ErrorKind.FieldNotFound, NotAnObject);
        }
    }

    private static Result<SecretValue> ToScalar(ref Utf8JsonReader reader, SecretReference reference, string field) =>
        reader.TokenType switch
        {
            JsonTokenType.String => Result<SecretValue>.Success(new SecretValue(reader.GetString()!)),
            JsonTokenType.Number => Result<SecretValue>.Success(new SecretValue(Encoding.UTF8.GetString(reader.ValueSpan))),
            JsonTokenType.True => Result<SecretValue>.Success(new SecretValue("true")),
            JsonTokenType.False => Result<SecretValue>.Success(new SecretValue("false")),
            _ => Fail(reference, ErrorKind.FieldNotFound, $"The field '{field}' is not a scalar value (string, number or boolean)."),
        };

    private static Result<SecretValue> Fail(SecretReference reference, ErrorKind kind, string detail) =>
        Result<SecretValue>.Failure(new Error(kind, reference.Path, detail));
}
