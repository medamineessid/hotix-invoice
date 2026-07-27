# HOTIX Invoice Extractor — Résumé Technique

Système local Windows d'extraction de factures : backend Python (FastAPI + PaddleOCR + Gemini/Grok AI), client WPF (C#, .NET 8, MVVM), installateur Inno Setup. Monitoring via Sentry.

---

## Architecture

```
┌────────────────────────────────────────────────────────────┐
│                   Utilisateur (Windows)                    │
├────────────────────────────────────────────────────────────┤
│                                                             │
│   ┌──────────────────────────────┐  ┌──────────────────┐   │
│   │   Hotix.InvoiceClient.exe    │  │ HotixDiagnostics  │   │
│   │   (WPF C#, .NET 8)           │  │ (Post-install)    │   │
│   │   - Design System Premium    │  │                   │   │
│   │   - 11 dictionnaires de thème│  │  7 services vérif │   │
│   │   - MVVM (MainViewModel)     │  │  4 actions réparation│  │
│   │   - Traduction EN/FR         │  │                   │   │
│   └─────────────┬────────────────┘  └──────────────────┘   │
│                 │ HTTP (127.0.0.1:8000)                     │
│   ┌─────────────▼──────────────────────────────────────┐   │
│   │              Serveur Python (FastAPI)               │   │
│   │  ┌──────────┐  ┌──────────┐  ┌──────────────────┐  │   │
│   │  │  Gemini  │  │   Grok   │  │  PaddleOCR       │  │   │
│   │  │  Vision  │  │  Vision  │  │  (hors ligne)    │  │   │
│   │  └──────────┘  └──────────┘  └──────────────────┘  │   │
│   └─────────────────────────────────────────────────────┘   │
│                                                             │
└─────────────────────────────────────────────────────────────┘
```

### Flux d'exécution

1. **Démarrage**: App → splash → processus Python (uvicorn)
2. **Health check**: Interroge `/health` jusqu'à 90s, rapports de progression
3. **Preflight**: `server/verify_system.py` (Python, Poppler, paquets)
4. **Onboarding**: Visite guidée 5 étapes (première exécution)
5. **Extraction**: Fichiers → moteur → `/extract` → grille résultats
6. **Cascade**: Mode Auto = Gemini → Grok → OCR local
7. **Export**: Filtres (Résultats/Manquants/Complets) → Excel via ClosedXML

---

## Structure du dépôt

| Répertoire | Objet | Fichiers clés |
|---|---|---|
| `server/` | Service FastAPI Python | `main.py`, `field_extractor.py`, `ocr_engine.py`, `gemini_extractor.py` |
| `client/` | App WPF C# | `MainWindow.xaml`, `MainViewModel.cs`, `Themes/*.xaml` |
| `client/ViewModels/` | ViewModels MVVM | `MainViewModel.cs` (~1200 lignes), `InvoiceRowViewModel.cs` |
| `client/Themes/` | Design system (11 fichiers) | `Colors.xaml`, `Brushes.xaml`, `Animations.xaml`, `ButtonStyles.xaml` |
| `client/Resources/` | Traductions i18n | `strings.json` (EN), `strings.fr.json` (FR) |
| `client/Converters/` | Convertisseurs WPF | `ConfidenceToColorConverter.cs` (3 niveaux) |
| `client/HotixDiagnostics/` | Outil diagnostic WPF | `MainWindow.xaml.cs` (3 vérifications) |
| `installer/` | Installateur Inno Setup | `Hotix.iss` (600+ lignes Pascal), vendor/ |
| `scripts/` | Scripts d'automatisation | `setup.ps1`, `start.ps1`, `start.bat` |
| `server/tests/` | Tests unitaires Python | `test_field_extractor.py`, `test_ingestion.py`, `test_ocr_engine.py`, `test_utils.py` |

---

## Serveur Python

### `server/main.py` — Point d'entrée FastAPI

Moteur OCR géré par cycle de vie avec recyclage mémoire.

**Endpoints:**
- `GET /health` → `{"status": "ok"}`
- `GET /engine-status` → `{"gemini_available": bool, "ocr_available": bool}`
- `POST /extract` → `InvoiceExtractionResponse` (upload multipart)
- `POST /validate-gemini-key` → Validation clé via appel API réel
- `POST /validate-grok-key` → Validation clé via API x.ai
- `POST /admin/recycle-engine` → Recyclage forcé OCR

**Choix d'architecture:**
- **Recyclage OCR**: Toutes les 25 requêtes, libère et recrée PaddleOCR (`OCR_ENGINE_RECYCLE_INTERVAL=25`)
- **Sémaphore**: `asyncio.Semaphore(1)` sérialise les opérations OCR
- **Préchauffage**: Modèle PaddleOCR chargé au démarrage (pas à la 1ère requête)
- **Sentry**: DSN via variable d'environnement `SENTRY_DSN`
- **CORS**: Verrouillé sur `127.0.0.1:8000`, méthodes GET/POST uniquement
- **Timeout 90s**: Messages de progression par phase pendant le démarrage
- **Transparence moteur**: `engine_used: Literal["gemini", "grok", "ocr"]` dans la réponse

### `server/field_extractor.py` — Extraction heuristique

30+ fonctions pour l'extraction de champs sur OCR bruité :
- Association étiquette/valeur (même ligne, ligne suivante, bloc)
- Scoring géométrique (proximité verticale/horizontale, alignement)
- Validation croisée et réconciliation des montants
- Alias français/tunisien/anglais pour les champs de facture
- Nettoyage des valeurs numériques et normalisation des dates

### `server/gemini_extractor.py` — Client API Gemini/Grok

- SDK `google.genai` (migré de `google.generativeai` déprécié)
- Clé API depuis `SENTRY_GEMINI_KEY` ou `appsettings.json`
- Messages d'erreur en français
- Modèle configurable : `gemini-2.5-flash`, `gemini-2.0-flash`, `gemini-1.5-pro`

### `server/ocr_engine.py` — Wrapper PaddleOCR

- Chargement différé (lazy loading) du modèle
- Français : `lang='fr'`
- Compatible PaddleOCR 3.7.0+ (paramètres dépréciés supprimés)
- `paddlepaddle==3.2.0` épinglé

### `server/ingestion.py` — Chargement des fichiers

- `load_invoice_images()` : PDF via Poppler/pdf2image, images via Pillow
- Extensions supportées : `.pdf`, `.jpg`, `.jpeg`, `.png`, `.bmp`, `.tif`, `.tiff`

---

## Client C#

### `client/Hotix.InvoiceClient.csproj`

- Cible : `net8.0-windows`
- WPF, using implicites, nullable activé
- NuGet : `ClosedXML 0.104.0`, `Sentry 6.6.0`
- **BuildInfo.g.cs** : Hash git auto-généré à la compilation
- **CopyAssets** : Copie `server/*.py`, `appsettings.json`, `Resources/*.json` après build

### `client/App.xaml.cs` — Bootstrap

- Initialisation Sentry DSN
- Résolution du chemin Python
- Démarrage du serveur avec polling santé (90s max)
- Splash screen → santé OK → fenêtre principale
- Nettoyage à la fermeture (kill processus Python)
- Gestionnaire d'exceptions global (Sentry + MessageBox)

### `client/MainWindow.xaml` — UI principale (2 600 lignes)

**Disposition :** Grille 3 lignes + 3 colonnes

**Sections :**
1. Barre de titre personnalisée (drag, boutons, toggle langue FR/EN)
2. Barre latérale (navigation, liste fichiers, version, statut serveur)
3. Panneau de contrôle (sélecteur moteur, bouton ⚙ paramètres)
4. Cartes d'étapes (Étape 1 dossier, Étape 2 ajout fichiers)
5. Bannière de synthèse (résultats extraction, relance des erreurs)
6. Onglets résultats (Résultats + Incomplets, transition fade 150ms)
7. DataGrid (10 colonnes, tris, badges confiance pastel, badge moteur)
8. Panneau d'aperçu (texte OCR brut, repliable)
9. Barre d'état (statut serveur, progression)
10. Overlay d'erreur serveur (écran rouge + bouton réessayer)
11. Onboarding (5 étapes avec spotlight + callot, première exécution)
12. Overlay glisser-déposer (feedback visuel)
13. Notification mise à jour (barre GitHub Releases)

### `client/MainWindow.xaml.cs` — Code-behind (~600 lignes)

- `ResultsGrid_LoadingRow`: Animation de lignes étagées (opacité + glissement, 40ms décalage)
- `ShowAboutDialog()`: Boîte À propos avec badges moteur (PaddleOCR, Gemini, Grok)
- `TabResults_Click` / `TabIncomplete_Click`: Transition fade 150ms
- `CheckForUpdateAsync()`: Vérification GitHub Releases (cache 24h)
- Onboarding complet avec positionnement spotlight et callout

### `client/ViewModels/MainViewModel.cs` — ViewModel central (~1200 lignes)

**19 commandes :** BrowseFolder, BrowseFiles, StartExtraction, CancelExtraction, ExportExcel, Clear, Rerun, RerunAllErrors, ToggleAllFiles, ToggleAllRows, ClearSelectedRow, OpenSavedFolder, OpenSavedFile, RetryServer, ToggleSettings, SaveGeminiKey, ClearGeminiKey, SaveGrokKey, ClearGrokKey

**Gestion des moteurs :**
- `SelectedEngine`: "auto" | "gemini" | "grok" | "ocr"
- Polling DispatcherTimer 45s pour `/engine-status`
- API Gemini REST : `https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent`
- API Grok REST : `https://api.x.ai/v1/chat/completions`
- Parallélisme batch : configurable (`HOTIX_BATCH_CONCURRENCY`, défaut 4, max 16)

**Persistance :** `%APPDATA%\Hotix\settings.json`

### Design System Premium (11 fichiers)

| Fichier | Contenu |
|---|---|
| `Colors.xaml` | 30 couleurs : fonds chauds (#F8F7F4), accent (#D9472B), fonctionnel, badges pastel |
| `Brushes.xaml` | Pinceaux + 5 ombres Apple-style (ShadowCard, ShadowButton, ShadowDialog, etc.) |
| `Typography.xaml` | Échelle 11-32px, 5 graisses, polices Inter/Helvetica/Segoe UI |
| `Spacing.xaml` | Grille 8px, rayons 8/12/16/9999, paddings prédéfinis |
| `Animations.xaml` | 14 storyboards (150-250ms, easing cubique) |
| `ButtonStyles.xaml` | 4 styles + TemplateButtonBase (hover lift, press scale 0.97, `TargetType="ButtonBase"`) |
| `InputStyles.xaml` | TextBox, PasswordBox, ComboBox, CheckBox, RadioButton (44px, radius 12) |
| `CardStyles.xaml` | Carte radius 16, hover lift 1px |
| `DataGridStyles.xaml` | En-tête triable, badges pastel, lignes 48px |
| `DialogStyles.xaml` | Modal fade + scale (0.95→1.0, 250ms) |
| `NavigationStyles.xaml` | Barre latérale 240px, nav active soft red |

---

## Installateur (`installer/Hotix.iss`)

600+ lignes Pascal Script pour Inno Setup 6.3+.

9 fonctionnalités clés vérifiées :
1. **Détection Python multi-méthode** : PATH → registre → installateur intégré
2. **Réessai pip** : 3 tentatives avec backoff exponentiel (1s, 2s, 4s)
3. **Progression visible** : `WizardForm.StatusLabel` entre les étapes
4. **Journalisation** : `SaveStringToFile` horodaté dans `{app}\install.log`
5. **Vérification Internet** : `InternetGetConnectedState` de `wininet.dll`
6. **Validation requirements.txt** : `FileExists`
7. **Vérification version Python** : Parse `python --version`, accepte 3.8+
8. **Espace disque** : 2 200 MB minimum
9. **Rollback** : Suppression complète du venv en cas d'échec

---

## Corrections de bugs majeurs

| Bug | Fichier(s) | Description | Statut |
|---|---|---|---|
| DOTNET-A | `ButtonStyles.xaml` | `Cursor="NotAllowed"` → `"No"` (enum WPF invalide) | ✅ |
| DOTNET-B | `strings.json`, `.fr.json` | Clé `ClearConfirmMessage` manquante (FormatException) | ✅ |
| DOTNET-C | `ButtonStyles.xaml` | Chaîne `TargetType="Button"` → `"ButtonBase"` | ✅ |
| StaticResource | `ButtonStyles.xaml` | TemplateButtonBase et 3 styles → ButtonBase | ✅ |
| SDK Gemini | `gemini_extractor.py` | `google.generativeai` → `google.genai` | ✅ |
| PaddleOCR 3.x | `ocr_engine.py` | Paramètres dépréciés supprimés | ✅ |
| Transparence | API + Client | `engine_used`, badge, colonne Excel | ✅ |
| BuildInfo | `.csproj` | Hash git auto-généré | ✅ |
| Curseur XAML | `ButtonStyles.xaml` | `NotAllowed` → `No` (XamlParseException) | ✅ |

---

## État actuel

✅ **Build**: 0 erreurs (Debug + Release)
✅ **Tests**: Pytest passe (field_extractor, ingestion, ocr_engine, utils)
✅ **Installateur**: Compile (~50 MB)
✅ **Sentry**: 3 bugs critiques corrigés (DOTNET-A, B, C)
✅ **Client**: Design system premium, animations, onboarding, multi-moteur
✅ **i18n**: Anglais + Français, bascule en un clic

---

## Notes de maintenance

1. **`server.main`** doit rester importable comme module de package (imports relatifs)
2. **ViewModel unique** — Créé dans `App.xaml.cs`, passé à `MainWindow`
3. **Clé API Gemini** — Un seul chemin de sauvegarde via `ResolveAppSettingsPath()`
4. **Button TargetType** — Tous les styles nommés utilisent `ButtonBase` (pas `Button`)
5. **BuildInfo.g.cs** — Auto-généré, dans .gitignore
6. **paddlepaddle==3.2.0** épinglé (requis par PaddleOCR 3.7.0)
