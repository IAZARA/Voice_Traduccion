using VoiceTraductor.Core;

namespace VoiceTraductor.Infrastructure.Realtime;

public sealed class TranslationStreamFactory : ITranslationStreamFactory
{
    public ITranslationStream Create(TranslationDirection direction) =>
        new RealtimeTranslationStream(direction);
}

public sealed class OpenAiApiKeyValidator(ITranslationStreamFactory streamFactory)
    : IApiKeyValidator
{
    public async Task ValidateAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new TranslationException(
                TranslationErrorKind.Authentication,
                "La API key está vacía.",
                false);
        }

        await using var stream =
            streamFactory.Create(TranslationDirection.IncomingEnglishToSpanish);
        await stream.ConnectAsync(
                new TranslationStreamOptions(
                    TranslationDirection.IncomingEnglishToSpanish,
                    apiKey.Trim(),
                    "en",
                    "es",
                    null),
                cancellationToken)
            .ConfigureAwait(false);
        await stream.CloseAsync(cancellationToken).ConfigureAwait(false);
    }
}
