using System.Globalization;

namespace Client.Services;

/// <summary>
/// Texts for the eListings landing / sign-in page in UK English, US English, German and French.
/// The rest of the app is English only for now.
/// </summary>
public static class LandingText
{
    public const string Default = "en-gb";

    public static readonly IReadOnlyList<(string Code, string Name)> Languages = new[]
    {
        ("en-gb", "English (UK)"),
        ("en-us", "English (US)"),
        ("de", "Deutsch"),
        ("fr", "Français")
    };

    private static readonly Dictionary<string, Dictionary<string, string>> Texts = new()
    {
        ["en-gb"] = new()
        {
            ["badge"] = "eBay listing automation",
            ["headline1"] = "From supplier catalogue",
            ["headline2"] = "to profitable eBay listings.",
            ["lead"] = "Import products from your suppliers, see the real profit after eBay fees and VAT, publish in bulk and keep stock and prices in sync – all in one place.",
            ["f1.title"] = "Supplier catalogues",
            ["f1.body"] = "Import products, trade prices and stock straight from your suppliers.",
            ["f2.title"] = "True profit",
            ["f2.body"] = "eBay fees, VAT and delivery worked out before you list.",
            ["f3.title"] = "Bulk publishing",
            ["f3.body"] = "Create and publish hundreds of listings in one go.",
            ["f4.title"] = "Sales & stock sync",
            ["f4.body"] = "Orders, cost of sales and stock levels kept up to date.",
            ["by"] = "A Digital Club product",
            ["signin.title"] = "Welcome back",
            ["signin.sub"] = "Sign in to your eListings account.",
            ["email"] = "Email",
            ["password"] = "Password",
            ["signin"] = "Sign in",
            ["signing"] = "Signing in…",
            ["missing"] = "Please enter your email and password.",
            ["noaccount"] = "No account? Ask your administrator to invite you.",
            ["want"] = "Want eListings for your business?",
            ["contact"] = "Talk to Digital Club",
            ["language"] = "Language"
        },
        ["en-us"] = new()
        {
            ["lead"] = "Import products from your suppliers, see the real profit after eBay fees and sales tax, publish in bulk and keep stock and prices in sync – all in one place.",
            ["f2.body"] = "eBay fees, taxes and shipping calculated before you list.",
            ["f4.body"] = "Orders, cost of sales and inventory kept up to date."
        },
        ["de"] = new()
        {
            ["badge"] = "eBay-Angebote automatisieren",
            ["headline1"] = "Vom Lieferantenkatalog",
            ["headline2"] = "zum profitablen eBay-Angebot.",
            ["lead"] = "Importieren Sie Produkte Ihrer Lieferanten, sehen Sie den echten Gewinn nach eBay-Gebühren und Mehrwertsteuer, veröffentlichen Sie in großen Mengen und halten Sie Bestand und Preise synchron – alles an einem Ort.",
            ["f1.title"] = "Lieferantenkataloge",
            ["f1.body"] = "Produkte, Händlerpreise und Bestand direkt von Ihren Lieferanten importieren.",
            ["f2.title"] = "Echter Gewinn",
            ["f2.body"] = "eBay-Gebühren, MwSt. und Versand berechnet, bevor Sie einstellen.",
            ["f3.title"] = "Massen-Veröffentlichung",
            ["f3.body"] = "Hunderte Angebote auf einmal erstellen und veröffentlichen.",
            ["f4.title"] = "Verkäufe & Bestand",
            ["f4.body"] = "Bestellungen, Wareneinsatz und Bestände immer aktuell.",
            ["by"] = "Ein Produkt von Digital Club",
            ["signin.title"] = "Willkommen zurück",
            ["signin.sub"] = "Melden Sie sich bei Ihrem eListings-Konto an.",
            ["email"] = "E-Mail",
            ["password"] = "Passwort",
            ["signin"] = "Anmelden",
            ["signing"] = "Anmeldung…",
            ["missing"] = "Bitte geben Sie E-Mail und Passwort ein.",
            ["noaccount"] = "Kein Konto? Bitten Sie Ihren Administrator um eine Einladung.",
            ["want"] = "eListings für Ihr Unternehmen?",
            ["contact"] = "Sprechen Sie mit Digital Club",
            ["language"] = "Sprache"
        },
        ["fr"] = new()
        {
            ["badge"] = "Automatisation des annonces eBay",
            ["headline1"] = "Du catalogue fournisseur",
            ["headline2"] = "aux annonces eBay rentables.",
            ["lead"] = "Importez les produits de vos fournisseurs, voyez la vraie marge après frais eBay et TVA, publiez en masse et gardez stocks et prix synchronisés – au même endroit.",
            ["f1.title"] = "Catalogues fournisseurs",
            ["f1.body"] = "Importez produits, prix professionnels et stocks directement de vos fournisseurs.",
            ["f2.title"] = "Vraie marge",
            ["f2.body"] = "Frais eBay, TVA et livraison calculés avant la mise en ligne.",
            ["f3.title"] = "Publication en masse",
            ["f3.body"] = "Créez et publiez des centaines d'annonces en une fois.",
            ["f4.title"] = "Ventes et stocks",
            ["f4.body"] = "Commandes, coût des ventes et niveaux de stock toujours à jour.",
            ["by"] = "Un produit Digital Club",
            ["signin.title"] = "Bon retour",
            ["signin.sub"] = "Connectez-vous à votre compte eListings.",
            ["email"] = "E-mail",
            ["password"] = "Mot de passe",
            ["signin"] = "Se connecter",
            ["signing"] = "Connexion…",
            ["missing"] = "Veuillez saisir votre e-mail et votre mot de passe.",
            ["noaccount"] = "Pas de compte ? Demandez une invitation à votre administrateur.",
            ["want"] = "eListings pour votre entreprise ?",
            ["contact"] = "Contacter Digital Club",
            ["language"] = "Langue"
        }
    };

    /// <summary>Text for a key; falls back to UK English, then to the key itself.</summary>
    public static string Get(string language, string key)
        => Texts.TryGetValue(language, out var own) && own.TryGetValue(key, out var text) ? text
         : Texts[Default].TryGetValue(key, out var fallback) ? fallback
         : key;

    /// <summary>A supported language code from "?lang=" or the browser culture (e.g. "de-AT" → "de", "en-US" → "en-us").</summary>
    public static string Resolve(string? requested)
    {
        var candidates = new[] { requested, SafeCultureName() };
        foreach (var raw in candidates)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var code = raw.Trim().ToLowerInvariant().Replace('_', '-');
            if (Texts.ContainsKey(code)) return code;
            if (code.StartsWith("en-us")) return "en-us";
            if (code.StartsWith("en")) return "en-gb";
            var two = code.Length >= 2 ? code[..2] : code;
            if (Texts.ContainsKey(two)) return two;
        }
        return Default;
    }

    private static string? SafeCultureName()
    {
        try { return CultureInfo.CurrentUICulture.Name; }
        catch (CultureNotFoundException) { return null; }
    }
}
