# P07 — Managed C# derleme ve davranış doğrulaması: ilk dilim

Bu dilim, kaynak kodu depoda bulunan küçük bir managed fixture için **kaynak → DLL → ortak ILSpy hattı → C# → DLL → davranış karşılaştırması** yapar. Üretim export raporundaki `Succeeded`, hâlâ decompiler hata durumudur; bu testin compile/behavior sonucu ayrıca `managed-fixture-report.json` içinde tutulur.

Bu belge ilk dilimin sözleşmesidir. Sonraki [dil seçimi ve Unity referans profili dilimi](SCRIPT_LANGUAGE_PROFILE.tr.md), iki gerçek export yolunu ve Unity 6000.4.0f1 referanslarıyla compile kontrolünü ayrıca ekler. Aşağıdaki ilk dilim .NET 10 fixture'ına ait davranış kanıtı, Unity fixture'ına aktarılmaz.

## Profil ve kapsam

| Ayar | Değer |
|---|---|
| Kaynak | `Source/AssetRipper.Tests/Fixtures/ManagedRoundTrip.cs`, embedded resource; normal test assembly'sine derlenmez |
| Referans profili | `ControlledFixture.Net100`, `Basic.Reference.Assemblies.Net100` **1.8.12** |
| C# derleyicisi | `Microsoft.CodeAnalysis.CSharp` **5.9.0**, üretimde kullanılan ILSpy paketi aynı kalır |
| Dil/optimizasyon matrisi | C# **7.3 / 9**, her biri Debug / Release |
| Seçenekler | DLL, deterministic, checked arithmetic, unsafe kapalı, nullable kapalı; implicit using yok |
| Davranış ortamı | Test runner'ın .NET 10 runtime'ı; orijinal ve tekrar derlenmiş DLL ayrı collectible `AssemblyLoadContext` içinde |
| Unity Editor | `NotRun` |

**Bu referans paketi bir Unity API profili değildir.** Araç uygulamasının `net10.0` hedefi, kurtarılan oyunun hedefi olarak kullanılmıyor. Burada açıkça tanımlanan bir .NET 10 fixture test ediliyor. Unity referans DLL'leri, API compatibility seçimi ve Editor doğrulaması ayrı kabul kapılarıdır. Dinamik derleme/yükleme yalnız `IsAotCompatible=false` olan test projesindedir; ürünün AOT bağımlılıklarına Roslyn eklenmez.

Referanslar yalnız pinli paketin gömülü image'larından alınır. Host `TRUSTED_PLATFORM_ASSEMBLIES`, SDK taraması ve örtük referans fallback'i yoktur. Rapor her referans dosyasının adını ve SHA-256 değerini, kaynak/çıktı hash'lerini, gerçek compiler/decompiler sürümünü, runtime/OS ve kullanılan seçenekleri kaydeder.

## Ayrı kabul kapıları

1. Orijinal fixture belirtilen profil ile derlenir. Derleyici uyarı/hataları kimlik, dosya, satır ve sütunuyla tutulur.
2. DLL, üretimde paylaşılan `ScriptDecompilation.Run` / `ILSpyWholeProjectDecompiler` üzerinden çözülür. Assembly metadata'sı aynı referans paketinden sağlanır; resolver diagnostics rapordadır.
3. Üretilen tüm `.cs` dosyaları, `Properties/AssemblyInfo.cs` dahil, aynı profil ile tekrar derlenir.
4. Public tip, interface, field, constructor, method, property ve event yapısı karşılaştırılır. Tipler, generic constraints, ref/out, parametre adları ve erişilebilir getter/setter bilgileri kapsanır. Eksik/fazla imzalar ayrı listelenir.
5. **20 kontrollü girdi** iki DLL üzerinde çalışır: state/closure/event unsubscribe, branch/loop, iterator ve iterator exception, async sonuç/exception, generic seçim/null, explicit interface, ref/out yan etkileri, catch/finally, Unicode adlar ve checked overflow. Sonuç, çağrı sonrası argümanlar, exception tipi/parametresi/mesajı karşılaştırılır. Orijinal DLL ayrıca elle yazılmış beklenen sonuçlarla kontrol edilir; iki yanlış sonuç eşit diye kabul edilmez.

Yürütülmeyen kapı `NotRun` kalır. Derleme hatasında contract ve behavior çalışmaz. Sıfır davranış vakası başarı oluşturmaz. Genel başarı bütün bu kapıların geçmesini ve beklenen vaka sayısını gerektirir. Bu eşdeğerlik yalnız belirtilen girdiler için kanıttır; tüm programın semantik eşdeğerliği veya yeniden üretilen IL'nin byte eşitliği iddia edilmez.

Negatif kontroller derlenen bir `return 0` stub'ının davranışta, kullanılmayan public alan silinmesinin contract kontrolünde başarısız olduğunu gösterir. Tanımsız ad derleme hatası sonraki kapıları `NotRun` bırakır. C# 9 `record` sözdizimi C# 7.3 profilinde reddedilir; `System.Runtime.dll` kaldırıldığında host runtime referans eksikliğini gizleyemez.

## Bu testin ortaya çıkardığı resolver düzeltmesi

Pinli ILSpy 11, `System.Runtime.InteropServices` ve `System.Runtime.CompilerServices.Unsafe` için metadata'da bulunmayan, public key token içermeyen iki `AssemblyNameReference` sorgusu ekler. Önceki strict kimlik kontrolü, yüklü imzalı framework DLL'lerini bu isteğe bağlı sorgular için `IdentityMismatch` sayabiliyordu. Kaynak: [pinli fork'ta DecompilerTypeSystem](https://github.com/AssetRipper/ILSpy/blob/c9f90082e63c846d974349831881d90d79741c98/ICSharpCode.Decompiler/TypeSystem/DecompilerTypeSystem.cs).

Resolver yalnız bu iki adın token/culture içermeyen sentetik sorgusunda, tam eşleşme bulunmazsa `ImplicitFrameworkProbeUnresolved` uyarısı ve `null` döndürür. Publisher tahminiyle bir DLL seçilmez. Gerçek PE metadata referansları ve diğer sorgular eski name/version/culture/token kontrolünden geçer. Aynı ada sahip zorunlu unsigned referansın signed DLL'e bağlanmaması ayrıca test edilir. Eksik bir optional probe, gerekli metadata bağımlılığının çözülmüş olduğunu kanıtlamaz.

## Çalıştırma ve kanıtlar

```sh
dotnet test Source/AssetRipper.Tests/AssetRipper.Tests.csproj \
  --filter TestCategory=ManagedRoundTrip --logger trx \
  --results-directory TestResults
```

Her roundtrip çalışması test output dizininde benzersiz bir `TestResults/managed-roundtrip/<profil>-<optimizasyon>-<mutation>-<uuid>/` klasörü oluşturur. İçinde özgün `.cs`/DLL, tekrar derlenen `.cs` dosyaları/DLL ve JSON raporu vardır. JSON raporu NUnit attachment olarak eklenir; CI mevcut test işinde TRX ve bu klasörleri artifact olarak saklar. Negatif kontrol klasörleri `Mutation` alanıyla açıkça ayrılır; bunların JSON'unda `Succeeded=false` beklenen sonuçtur, NUnit testi bu tespitin doğruluğunu kontrol eder.

## Yerel doğrulama sonucu

| Kontrol | Sonuç |
|---|---|
| Tüm solution Debug | **621/621 geçti**, 8 test projesi |
| İlgili Release testleri | **92/92 geçti**, 3 test projesi; core 85, IO 3, GUI 4 |
| Solution Release build | Başarılı, **0 uyarı / 0 hata** |
| Yeni NUnit vakaları | 9 compile/roundtrip + 4 resolver = **13** |
| Sağlam fixture matrisi | 4 profil/optimizasyon birleşimi; her birinde 20/20 davranış, contract ve compile geçti |
| Negatif kontroller | Stub: behavior başarısız; alan silme: contract başarısız; tanımsız ad: compile başarısız, sonraki kapılar `NotRun` |
| Kanıt dosyaları | Debug/Release raporlarındaki kaynak ve DLL hash'leri, diskteki dosyalardan bağımsız tekrar hesaplanıp eşleşti |

Test runner Debug/Release yapılandırması, fixture'ın kendi optimizasyon matrisinden ayrıdır; iki runner yapılandırmasında da fixture'ın dört birleşimi sınandı. Referans profili her birleşimde aynıdır. İlgili Release testlerinin geçmesi tüm Release suite'in geçtiği anlamına gelmez; önceden bulunan Debug-assert test sınırı [baseline notlarında](DECOMPILATION_BASELINE.tr.md) kayıtlıdır. GitHub Actions artifact yüklemesi yapılandırıldı; uzak CI işi bu yerel çalışmada tetiklenmedi.

## Sınırlar ve sonraki iş

- Davranış yürütmesi yalnız depodaki kaynak sahibi olduğumuz fixture içindir. SystemTester ve kullanıcıdan alınan oyun DLL'leri için otomatik execution eklenmedi. `AssemblyLoadContext` bir güvenlik sandbox'ı değildir.
- Fixture gerçek eski Mono/Unity derleyicisinden gelmez. Şimdiki Roslyn tarafından iki dil sürümünde üretilen managed IL'nin roundtrip testidir; `MonoManager`, asset assembly processors ve iki tam export ürününün compile/behavior testi henüz kapsamda değildir.
- Public contract karşılaştırması protected/private alanların, Unity serialization attribute'larının, custom attribute payload'larının, layout/default değerlerinin ve tüm metadata'nın eşitliği değildir. Bu fixture'da TypeTree/MonoBehaviour payload veya UnityEngine referansı yoktur.
- Gerçek Unity sürümüne ait sabit referans setleri ve orijinal Mono çıktıları eklendikten sonra profil matrisi genişletilecek. Export dil ayarlarının otomatik eşlemesi iki export yolunda ayrıca sınanacak; tam Unity Editor import/scene/ref doğrulaması ve IL2CPP metot recovery'si bu testle tamamlanmış sayılmaz.
