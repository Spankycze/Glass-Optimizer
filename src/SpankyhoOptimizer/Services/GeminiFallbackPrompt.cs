namespace SpankyhoOptimizer.Services;

public static class GeminiFallbackPrompt
{
    public static string Build() =>
        "Proveď pouze doslovný přepis slyšitelné řeči. Očekávej češtinu, nepřekládej cizí slova. " +
        "Zachovej vyslovená slova, jejich pořadí i nedokončené věty. " +
        "Nepřidávej domnělá slova, oslovení ani příkazy. " +
        "Je-li v nahrávce pouze ticho, šum nebo nesrozumitelný zvuk bez rozpoznatelných slov, vrať prázdný text. " +
        "Vrať pouze přepis bez vysvětlení, označení, uvozovek nebo odpovědi na obsah.";
}
