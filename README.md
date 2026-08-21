#  HOTIX — Extraction de factures

> Application Windows locale d'extraction de factures PDF et images par OCR, avec moteurs cloud optionnels, interface WPF et export Excel.

[![Build](https://github.com/medamineessid/hotix-invoice/actions/workflows/build-check.yml/badge.svg)](https://github.com/medamineessid/hotix-invoice/actions)
[![Sentry](https://img.shields.io/badge/monitoring-Sentry-6B5B95)](https://must-ap.sentry.io)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](installer/LICENSE.txt)

---

## État actuel

| Élément | État vérifié |
|---|---|
| Version client / installateur | **1.0.1** |
| Version API | **1.0.0** |
| Branche | `master` synchronisée avec `origin/master` |
| Dernier commit documenté | `653654c` — `ci: add build-and-release workflow for installer` |
| Tests C# locaux | **103 passed**, 1 skipped, 0 failed — .NET 8 (`client-tests/`) |
| Tests Python locaux | voir `pytest server/tests/ -q` — non re-chronométré à cette révision |
| GitHub Actions (build) | `build-check.yml` — dotnet build/test + pytest sur chaque push/PR vers `master` |
| GitHub Actions (release) | `release-installer.yml` — build + publication GitHub Release sur tag `v*`, pas encore déclenché |

Les résultats ci-dessus correspondent à la dernière validation effectuée le **21 août 2026** sur le commit `653654c`. Une prochaine modification du dépôt doit rafraîchir cette ligne de référence.

---

## Fonctionnalités

| Fonctionnalité | Description |
|---|---|
| **OCR local** | PaddleOCR en français, sans clé API et utilisable hors ligne après installation des modèles |
| **Gemini Vision** | Extraction cloud Google Gemini, clé API requise |
| **Grok Vision** | Extraction cloud xAI/Grok, clé API requise |
| **Mode automatique** | Cascade côté client : Gemini → Grok → OCR local |
| **Extraction structurée** | Numéro, date, fournisseur, client, montants, lignes d'articles et récapitulatif TVA |
| **Validation métier** | Réconciliation HT/TVA/taxe/TTC, détection de collisions et score de confiance |
| **PDF et images** | PDF, JPG/JPEG, PNG, BMP, TIF/TIFF |
| **Export Excel** | Résultats, extractions incomplètes, feuilles d'articles, récapitulatif TVA et mode append |
| **Grille éditable** | Correction manuelle avant export |
| **Multi-langue** | Français / anglais |
| **Glisser-déposer** | Fichiers et dossiers de factures |
| **Aperçu** | Aperçu image/PDF, zoom, texte OCR brut et cache local borné |
| **Onboarding** | Visite guidée au premier lancement |
| **Mises à jour** | Vérification quotidienne des GitHub Releases |
| **Diagnostics** | Outil WPF séparé pour vérifier Python, Poppler et le serveur |

### Flux d'extraction

```text
Sélection de fichiers
        │
        ▼
┌───────────────────────────┐
│ Client WPF                 │
│ Auto : Gemini → Grok → OCR│
└─────────────┬─────────────┘
              │ HTTP localhost
              ▼
┌───────────────────────────┐
│ FastAPI local             │
│ Ingestion + PaddleOCR    │
│ Extraction heuristique   │
└───────────────────────────┘
```

Le client appelle directement Gemini/Grok pour les parcours cloud. Le serveur FastAPI fournit principalement l'OCR local, le mode backend Gemini, l'aperçu, la santé du service et les validations de clés.

---

## Architecture

```text
hotix-invoice/
├── server/                         # Backend Python/FastAPI
│   ├── main.py                     # API, lifecycle OCR, rate limiting
│   ├── models.py                   # Schémas Pydantic
│   ├── ingestion.py                # PDF/images → pages PIL
│   ├── ocr_engine.py               # Wrapper PaddleOCR 3.x
│   ├── field_extractor.py          # Extraction heuristique et géométrique
│   ├── gemini_extractor.py         # Gemini côté serveur + normalisation JSON
│   ├── utils.py                    # Texte, montants, géométrie, réconciliation
│   ├── verify_system.py            # Preflight de l'environnement
│   └── tests/                      # Tests pytest
│
├── client/                         # Client desktop WPF/.NET 8
│   ├── App.xaml.cs                 # Démarrage/arrêt du serveur local
│   ├── MainWindow.xaml             # Interface principale
│   ├── ViewModels/MainViewModel.cs # Orchestration UI et batch
│   ├── InvoiceClient.cs            # Client HTTP de l'API locale
│   ├── ExcelWriter.cs              # Export ClosedXML
│   ├── Themes/                     # Design system XAML
│   ├── Resources/                  # Traductions EN/FR
│   └── HotixDiagnostics/           # Utilitaire de diagnostic
│
├── client-tests/                   # Tests xUnit du client
├── invoices/                       # Fixtures OCR et données d'évaluation
├── installer/                      # Script Inno Setup et documentation
├── scripts/                        # Setup et lancement Windows
├── requirements.txt                # Dépendances runtime Python
└── .github/workflows/              # CI GitHub Actions
```

### Serveur local

Le client démarre `uvicorn server.main:app` sur `127.0.0.1:8000`, attend le endpoint `/health`, puis affiche l'interface. Le serveur :

- préchauffe PaddleOCR au démarrage ;
- sérialise les opérations OCR avec un sémaphore ;
- recycle le moteur après 25 requêtes OCR pour limiter l'accumulation mémoire ;
- limite la taille d'un upload à 50 MB ;
- applique un timeout d'extraction configurable ;
- conserve un rate limit `/extract` de **100 requêtes/minute/IP par défaut**, configurable par `HOTIX_EXTRACT_RATE_LIMIT` ;
- conserve un rate limit séparé de **5 requêtes/minute/IP** pour la validation des clés.

---

## Installation utilisateur

### Prérequis

| Logiciel | Version / condition |
|---|---|
| Windows | Client WPF Windows uniquement |
| Python | **3.12+ recommandé et validé par `setup.ps1` et la CI** |
| .NET | .NET 8 Desktop Runtime / SDK |
| Poppler | Requis pour convertir les PDF |
| Espace disque | Environ **2 200 MB** pour l'installation et les dépendances OCR |

L'installateur peut rechercher plusieurs installations Python et applique un contrôle de version minimal, mais la combinaison réellement validée par `setup.ps1`, les dépendances Paddle et la CI est Python 3.12. Utilisez Python 3.12 pour éviter les incompatibilités de wheels natives ; les autres versions ne constituent pas une cible de développement validée.

### Installation depuis les sources

Depuis la racine du dépôt, dans PowerShell :

```powershell
# Vérifie Python 3.12+, Poppler et .NET 8,
# crée venv, installe requirements.txt et publie le client.
.\scripts\setup.ps1
```

Le script produit le client dans `client\publish\`.

### Lancement

```powershell
.\scripts\start.ps1
```

Ou, selon l'installation :

```text
scripts\start.bat
```

### Installateur Inno Setup

La version actuelle de l'installateur est **1.0.1** :

```text
HotixSetup_1.0.1.exe
```

L'installateur gère notamment :

- détection de Python par `py.exe`, `python.exe` et registre ;
- installation Python intégrée en dernier recours ;
- création du virtualenv ;
- installation de `requirements.txt` avec retries ;
- configuration Poppler ;
- journal `install.log` ;
- rollback du virtualenv en cas d'échec ;
- lancement de l'outil de diagnostics.

Pour compiler l'installateur, publier d'abord le client puis utiliser Inno Setup 6.3+ :

```powershell
dotnet publish client -c Release -o client\publish --self-contained false
& "C:\Program Files (x86)\Inno Setup 6\iscc.exe" installer\Hotix.iss
```

Le binaire Python `installer\vendor\python-3.12.6-amd64.exe` et les fichiers Poppler vendorisés doivent être présents pour une compilation complète.

---

## Utilisation

1. Lancez HOTIX.
2. Ajoutez des fichiers ou un dossier de factures.
3. Choisissez `Automatique`, `Gemini`, `Grok` ou `OCR local`.
4. Cliquez sur **Lancer l'extraction**.
5. Vérifiez les champs, les scores de confiance et les extractions incomplètes.
6. Corrigez les cellules si nécessaire.
7. Exportez vers Excel.

Raccourcis :

| Touche | Action |
|---|---|
| `F5` | Lancer l'extraction |
| `Escape` | Annuler l'extraction |
| `Ctrl+E` | Exporter vers Excel |

### Configuration Gemini / Grok

Ouvrez le bouton de paramètres près du moteur, saisissez la clé du fournisseur, choisissez le modèle puis validez. Les appels cloud nécessitent Internet ; l'OCR local ne nécessite pas de clé.

---

## Configuration et variables d'environnement

### Fichiers de configuration

- `%APPDATA%\Hotix\settings.json` : préférence de moteur, langue, onboarding et dernier contrôle de mise à jour.
- `%LOCALAPPDATA%\Hotix\appsettings.json` : configuration utilisateur et clés saisies dans le client. Le client chiffre les clés au repos avec DPAPI Windows et les déchiffre en mémoire pour les appels directs Gemini/Grok.
- `server/appsettings.json` : modèle de configuration livré avec les sources ; utilisé comme fallback/migration, sans clé secrète committée. Pour le chemin Gemini exécuté par Python, utilisez plutôt `GEMINI_API_KEY` ou une configuration serveur lisible par Python : le serveur ne déchiffre pas les valeurs DPAPI du client.

### Variables principales

| Variable | Défaut | Rôle |
|---|---:|---|
| `HOTIX_API_BASE_URL` | `http://127.0.0.1:8000` | URL de l'API locale/client |
| `HOTIX_BATCH_CONCURRENCY` | `4` | Parallélisme client, borné de 1 à 16 |
| `HOTIX_EXTRACT_RATE_LIMIT` | `100` | Requêtes `/extract` par minute et par IP |
| `HOTIX_EXTRACT_TIMEOUT_SECONDS` | `300` | Timeout d'une extraction serveur, minimum 10 s |
| `HOTIX_SERVER_START_TIMEOUT_SECONDS` | `90` | Timeout de démarrage du serveur local |
| `POPPLER_PATH` | auto-détection | Dossier des binaires Poppler |
| `GEMINI_API_KEY` | vide | Clé Gemini côté serveur, si utilisée |
| `SENTRY_DSN` | vide | Monitoring Sentry du serveur |

Ne mettez jamais de clé réelle dans Git, dans une fixture ou dans la documentation. Les clés cloud du parcours client direct sont envoyées au fournisseur choisi uniquement lorsque ce moteur est activé ; le parcours serveur Python suit sa propre configuration (`GEMINI_API_KEY`/`appsettings.json`).

---

## API FastAPI

Lancer le serveur depuis la racine du dépôt :

```powershell
python -m uvicorn server.main:app --host 127.0.0.1 --port 8000 --reload
```

| Endpoint | Méthode | Description |
|---|---|---|
| `/health` | GET | État OCR, Poppler et configuration cloud |
| `/engine-status` | GET | Disponibilité de l'OCR et présence de la clé Gemini |
| `/extract?engine=auto\|gemini\|ocr` | POST | Upload multipart et extraction structurée |
| `/preview/register` | POST | Enregistre temporairement un fichier pour aperçu |
| `/preview?token=...` | GET | Renvoie l'aperçu image/PDF associé au token |
| `/validate-gemini-key` | POST | Valide une clé Gemini auprès de Google |
| `/validate-grok-key` | POST | Valide une clé Grok auprès de xAI |
| `/admin/recycle-engine` | POST | Recycle le moteur OCR pour diagnostics |

### Exemple de réponse `/extract`

```json
{
  "numero_facture": "FAC-2024-001",
  "date": "01/01/2024",
  "fournisseur": "ABC SARL",
  "client": "Client X",
  "montant_ht": "1000.000",
  "montant_tva": "200.000",
  "montant_taxe": "10.000",
  "montant_ttc": "1210.000",
  "confidence": 0.92,
  "raw_text": "...",
  "engine_used": "ocr",
  "computed_fields": [],
  "amount_mismatch": false,
  "items": [],
  "tax_summary": []
}
```

---

## Développement et tests

### Installer les dépendances Python

```powershell
py -3.12 -m pip install -r requirements.txt
```

### Tests Python

```powershell
py -3.12 -m pytest server/tests/ -q
```

18 fichiers dans `server/tests/`. La suite couvre notamment l'ingestion, l'OCR normalisé,
l'extraction heuristique (`test_field_extractor.py`, `test_field_extractor_v5.py`), les
montants et la réconciliation (`test_utils.py`, `test_utils_v5.py`), les contrats de
schéma LLM, le rate limiting, les tokens d'aperçu et l'authentification admin.

### Build et tests C#

```powershell
dotnet build client/Hotix.InvoiceClient.csproj
dotnet test client-tests/Hotix.InvoiceClient.Tests.csproj
```

16 fichiers dans `client-tests/` (103 cas exécutables). Couvre l'export Excel, la
traduction des messages d'erreur, l'auto-détection de direction, le retry JSON, les
gardes de type `GetStringField`, la persistance d'`appsettings.json`, le contrat de
récapitulatif TVA et la concurrence de `TranslationSource`.

### Vérification des traductions

```powershell
python scripts/check_translations.py
```

### Autres harnais (hors CI, à lancer manuellement)

| Outil | Rôle |
|---|---|
| `server/eval_extraction.py` | Harnais d'évaluation OCR : correspondance exacte/tolérance par champ + précision/rappel par article avec alignement. Nécessite un jeu de vérité-terrain dans `evaluation/ground_truth/` (actuellement un seul exemple, pas encore de baseline réelle). |
| `scripts/check-xaml-resources.sh` | Vérifie que chaque `{StaticResource}`/`{DynamicResource}` référencé dans le XAML du client résout vers une clé définie. Filtre de première passe, pas une preuve de correction (ne vérifie ni l'ordre de fusion ni les ressources ajoutées en code-behind). |
| `client/TESTING_GUIDE.md` | Scénarios de repro manuelle pour ce qui ne peut pas être testé unitairement : fichier Excel verrouillé, perte réseau en cours d'extraction, arrêt du serveur local, etc. |

### CI GitHub Actions

Deux workflows dans `.github/workflows/` :

**`build-check.yml`** — sur `push` et `pull_request` vers `master` :
1. configure .NET 8 et Python 3.12 ;
2. vérifie l'intégrité des traductions ;
3. build le client ;
4. exécute les tests xUnit (`client-tests/`) ;
5. installe **l'intégralité de `requirements.txt`**, y compris PaddleOCR/PaddlePaddle ;
6. exécute `pytest server/tests/`.

**`release-installer.yml`** — sur tag `v*` ou déclenchement manuel :
1. publie le client WPF et l'outil de diagnostics (.NET 8, win-x64) ;
2. récupère les binaires vendorisés (Python, Poppler) via `installer/fetch-vendor.ps1` ;
3. installe Inno Setup et compile l'installateur ;
4. scanne la sortie de build à la recherche de clés API accidentellement embarquées (bloquant) ;
5. publie l'`.exe` compilé comme artefact de build, et comme asset de GitHub Release si déclenché par un tag.

---

## Historique Git récent

Les derniers commits expliquent l'état actuel du projet :

| Commit | Date | Changement |
|---|---|---|
| [`653654c`](https://github.com/medamineessid/hotix-invoice/commit/653654cbcf84501e1af2339deb43967ad7b49f7e) | 2026-08-21 | Workflow CI `release-installer.yml` (build + publication GitHub Release) |
| [`9875a1f`](https://github.com/medamineessid/hotix-invoice/commit/9875a1f) | 2026-08-21 | Tests de régression pour la persistance d'`appsettings.json` |
| [`9104cff`](https://github.com/medamineessid/hotix-invoice/commit/9104cff) | 2026-08-21 | Retrait du suivi git de l'installateur compilé (`installer/Output/`) |
| [`5fa683d`](https://github.com/medamineessid/hotix-invoice/commit/5fa683d) | 2026-08-21 | Correction d'un caractère corrompu (mojibake) dans `Hotix.iss` |
| [`a4a3974`](https://github.com/medamineessid/hotix-invoice/commit/a4a3974169b8373a855d3d82ea12c426023ff225) | 2026-08-20 | Correction du crash `File.Replace` quand `appsettings.json` n'existe pas encore |
| [`040ae0b`](https://github.com/medamineessid/hotix-invoice/commit/040ae0be5f6fc654023904aae96909ac246b57ca) | 2026-08-10 | CI alignée sur `requirements.txt` et rate limit local `/extract` porté à 100/configurable |
| [`98c2574`](https://github.com/medamineessid/hotix-invoice/commit/98c2574e699759cfca75e4d740db61a89e3002b4) | 2026-08-09 | CI dotnet + pytest et tests de régression LLM, direction et traduction |
| [`2348f79`](https://github.com/medamineessid/hotix-invoice/commit/2348f79c329979212c20abf633b36a23d1c80a56) | 2026-08-09 | Messages d'erreur utilisateur centralisés et traduits |
| [`1d1161e`](https://github.com/medamineessid/hotix-invoice/commit/1d1161e) | 2026-08-08 | Extraction directe Gemini/Grok, `responseSchema` et retry JSON |
| [`2561c5d`](https://github.com/medamineessid/hotix-invoice/commit/2561c5d) | 2026-08-08 | Correction du endpoint OCR et alignement de `tax_amount` |
| [`c4f6a97`](https://github.com/medamineessid/hotix-invoice/commit/c4f6a97) | 2026-08-08 | Export de toutes les lignes du récapitulatif TVA |
| [`6858a54`](https://github.com/medamineessid/hotix-invoice/commit/6858a54) | 2026-08-08 | Ajout de `unit` et `tax_summary`, correction de l'aperçu |
| [`7b1154a`](https://github.com/medamineessid/hotix-invoice/commit/7b1154a) | 2026-08-08 | Ajout de `ButtonGhostStyle` pour éviter une erreur XAML au chargement |
| [`964b914`](https://github.com/medamineessid/hotix-invoice/commit/964b914) | 2026-08-08 | Version client/installateur portée de 1.0.0 à 1.0.1 |
| [`38782c1`](https://github.com/medamineessid/hotix-invoice/commit/38782c1) | 2026-08-08 | Correction du crash `GetStringField` sur les nombres JSON |

---

## Sécurité et limites connues

- Le serveur écoute sur `127.0.0.1`, pas sur une interface réseau publique.
- Le CORS est limité aux origines localhost et aux méthodes GET/POST.
- Les clés cloud saisies par l'utilisateur sont stockées dans le profil utilisateur et chiffrées côté client avec DPAPI.
- Les fichiers envoyés à Gemini/Grok quittent la machine uniquement lorsque l'utilisateur active/configure le moteur cloud.
- Le rate limit `/extract` reste actif pour limiter une boucle ou un processus local défaillant ; il est de 100/60 par défaut.
- Les tokens d'aperçu sont temporaires et l'accès passe par une étape explicite d'enregistrement.
- PaddleOCR/PaddlePaddle sont lourds et le premier démarrage peut télécharger ou charger des modèles pendant plusieurs dizaines de secondes.
- Le client est Windows/WPF ; l'API Python peut être testée séparément, mais l'interface n'est pas multiplateforme.
- Le schéma API expose `engine_used` pour `gemini` et `ocr` côté serveur ; Grok est orchestré directement par le client WPF dans la cascade cloud.

Pour les scénarios UI, réseau et processus impossibles à couvrir uniquement par des tests unitaires, consulter [`client/TESTING_GUIDE.md`](client/TESTING_GUIDE.md).

---

## Licence

Projet sous licence MIT : [`installer/LICENSE.txt`](installer/LICENSE.txt).

## Remerciements

- [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) — OCR local
- [FastAPI](https://fastapi.tiangolo.com/) — API Python
- [ClosedXML](https://closedxml.github.io/ClosedXML/) — export Excel .NET
- [Inno Setup](https://jrsoftware.org/isinfo.php) — installateur Windows
- [Sentry](https://sentry.io/) — monitoring
- [Poppler](https://poppler.freedesktop.org/) — conversion PDF