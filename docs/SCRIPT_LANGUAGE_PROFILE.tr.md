# P07 — İki export yolunda dil seçimi ve gerçek Unity referansları

Bu ikinci dilim, primary-content export'taki otomatik dil seçimini Unity-project export ile eşitler. Ayrıca kaynak sahibi olduğumuz küçük bir `MonoBehaviour` örneğinin iki export yolundan çıkan C# kodunu, yerel Unity **6000.4.0f1** kurulumundan alınan sabit referans image'larıyla tekrar derler.

## Düzeltilen hata

Primary-content, `AutoSafe` / `AutoExperimental` ayarını Unity sürümünü vermeyen `ToCSharpLanguageVersion()` overload'ına iletiyordu. Bu overload `Latest` döndürüyordu. Unity 6000.4 için otomatik seçim C# 9 olmalı: testte primary-content C# 10 gerektiren file-scoped namespace üretti ve C# 9 derlemesi **CS8773** ile başarısız oldu. İlk regresyon çalışmasında sekiz export matrisi vakasının yedisi bu hatayı gösterdi; Unity-project yolu ve elle seçilen sürüm doğruydu.

- `ScriptLanguageSelection.ForUnity` iki yolda ortak seçim kaydı oluşturur. Primary-content hedef Unity sürümünü `GameData.ProjectVersion` üzerinden alır; stale/default `settings.Version` kullanmaz. Unity-project `ExportHandler` zaten bu sürümü settings'e aktarır.
- Mevcut AutoSafe/AutoExperimental sürüm tablosu korunur. Örneğin AutoSafe 2018.4 → C# 4, 2019.2 → 7.3, 2020.2 → 8, 2021.2.0b6 ve 6000.4 → 9. Alpha/beta sınırları ayrıca test edilir. Bu, dil politikasıdır; eski sürümlerin API referansları doğrulanmış sayılmaz.
- Elle seçilen dil, `Latest` dahil, korunur. Unity sürümü bilinmiyorsa otomatik seçim mevcut konservatif C# 4 politikasını kullanır ve bilinmeyen sürüm açıkça raporlanır.
- Primary-content `.csproj` dosyasının `LangVersion` değeri seçilen hedefe açıkça atanır. Doğrudan `ScriptExportCollection(extractor)` çağrısı da extractor'ın dilini korur.

Unity 6.4'ün C# 9 ve desteklenmeyen dil özellikleri [resmi compiler belgesinde](https://docs.unity3d.com/6000.4/Documentation/Manual/csharp-compiler.html), API compatibility seçenekleri [resmi profil belgesinde](https://docs.unity3d.com/6000.4/Documentation/Manual/dotnet-profile-support.html) açıklanır. Bu dilim compiler paketlerini yükseltmez ve bütün C# 9 özelliklerinin Unity runtime'ında desteklendiğini iddia etmez.

## Üretim raporu

Her iki yolun `script-export-report.json` dosyasına, schema 1'in ek alanı olarak şu seçim kaydı eklenir:

```json
{
  "LanguageSelection": {
    "UnityVersion": "6000.4.0f1",
    "Requested": "AutoSafe",
    "Effective": "CSharp9_0",
    "IsAutomatic": true,
    "UnityVersionUnknown": false
  }
}
```

Placeholder üreten Unity yolu da seçimi raporlar. Düşük seviyeli extractor doğrudan explicit dil ile oluşturulursa Unity sürümü `null` kalır; varsayılanı hâlâ explicit `Latest`'tır. Eski sürümsüz public overload geriye uyumluluk için durur; uygulamanın iki export yolunda otomatik seçim artık sürüm bilgisiyle yapılır.

Primary-content ayrıca `ProjectScaffoldFramework: "net10.0"` yazar. Bu **SDK proje iskeletinin** hedefidir; oyunun API profili değildir. Mevcut proje yazıcısı `NoStandardLib=True`, `DisableImplicitFrameworkReferences=True` ve assembly'ler arasında `ProjectReference` kullanır. Bu iskelet politikası değiştirilmedi. Yeni testler çıkarılan C# dosyalarını açık referans image'larıyla Roslyn üzerinden derler; bütün üretilen `.csproj` / `.slnx` grafiğinin `dotnet build` ile çalıştığını kanıtlamaz. Çakışan assembly kimlikleri için proje referanslarının retargeting'i de ayrı iştir.

Üretim raporundaki `Succeeded`, decompiler/placeholder/resolver sonucudur. Yeni dil kaydı bir compile veya davranış doğrulama sonucu değildir.

## Sabit Unity referans profili

[Unity6000.4.NetStandard21.json](../Source/AssetRipper.Tests/Fixtures/Unity6000.4.NetStandard21.json), **20** dosyanın göreli yolunu ve SHA-256 değerini sabitler:

- `NetStandard/ref/2.1.0/netstandard.dll`;
- `NetStandard/compat/2.1.0/shims/netfx` altındaki 17 compatibility shim;
- `UnityEngine.CoreModule.dll` ve `UnityEngine.SharedInternalsModule.dll`.

Profil adı `Unity6000.4.0f1.NetStandard21.CompileOnly`'dır. Yerel Editor sürümü `Unity -version` ile **6000.4.0f1** olarak doğrulandı. Unity DLL'lerinin assembly/file version bilgisi tek başına bu sürümü göstermediğinden içerik hash'leri zorunludur. Depoya Unity binary'leri eklenmedi; yalnız dosya adları ve hash'ler var. Başka bir kurulum veya platformun DLL'leri farklıysa ayrı, incelenmiş manifest gerekir; hash kontrolü gevşetilmez.

Referanslar `ASSETRIPPER_UNITY_EDITOR_DATA` ile belirtilen kurulumdan okunur. Her image hash kontrolünden geçmeden derleyiciye verilmez. Eksik dosya, farklı içerik veya geçersiz manifest hata üretir. Host TPA listesi, host `UnityEngine` projesi, SDK veya .NET 10 referanslarıyla tamamlama yapılmaz.

## Testin yaptığı kontrol

1. [UnityScriptSample.cs](../Source/AssetRipper.Tests/Fixtures/UnityScriptSample.cs), C# 9 ve sabit referans setiyle derlenir. `MonoBehaviour`, `Vector3`, `Transform`, private `[SerializeField]` alanları, target-typed `new` ve pattern matching kullanır. Kaynağın TargetFramework attribute'u `.NETStandard,Version=v2.1`'dir.
2. Referans metadata'sını resolver'a veren test manager'ı yalnız fixture assembly'sini export eder. İki gerçek export giriş noktası ve script raporları çalışır; BCL/Unity DLL'leri yeniden decompile edilmez. Assembly processing aşaması çalıştırılmaz.
3. Unity-project ve primary-content çıktıları AutoSafe/C# 9 ve explicit C# 7.3 altında yeniden derlenir. Primary-content assembly info dosyası dahil bütün C# dosyaları kontrol edilir. Dil seçimi ve `.csproj` `LangVersion` değeri sınanır.
4. Tekrar derlenen metadata'da `score: System.Int32` ve `offset: UnityEngine.Vector3` private alanlarının `UnityEngine.SerializeField` attribute'u ile kaldığı doğrulanır. Orijinal iki alan elle yazılmış oracle ile de kontrol edilir. Bu, serialize edilmiş payload veya initializer değerlerinin Unity'de doğrulanması değildir.
5. `Environment.ProcessId` örneği .NET 10 referanslarıyla geçerken Unity/.NET Standard 2.1 profilinde **CS0117** ile reddedilir. Eksik/değişmiş referans için ayrı negatif kontroller vardır.

`unity-reference-report.json`; profil manifestini, kaynak/çıktı hash'lerini, gerçek export dil kaydını, compiler diagnostics'lerini ve alan gözlemlerini içerir. `CompileAndFieldChecksPassed` yalnız bu kontrolleri kapsar. **Behavior=NotRun** ve **UnityEditorValidation=NotRun** açıkça tutulur. Test runner Unity DLL'lerini execution için yüklemez; native Unity çağrıları çalıştırılmaz. Editor sürümünü okumak, Editor import/scene testi değildir.

## Çalıştırma

```sh
dotnet test Source/AssetRipper.Tests/AssetRipper.Tests.csproj \
  --filter TestCategory=ScriptLanguageProfile

ASSETRIPPER_UNITY_EDITOR_DATA=/path/to/Unity/Editor/Data \
dotnet test Source/AssetRipper.Tests/AssetRipper.Tests.csproj \
  --filter TestCategory=UnityReferenceProfile --logger trx \
  --results-directory TestResults
```

Unity yolu verilmezse kurulum gerektiren **5 test `Skipped/NotRun`** olur; loader'ın iki bağımsız negatif testi yine çalışır. Yanlış yol veya hash verildiğinde test atlanmaz, başarısız olur. Normal CI bu kurulum olmadan Unity kontrolünü başarılı saymaz. Kaynak, DLL, `.csproj`, üretim script raporu ve compile raporu test output dizinindeki `TestResults/unity-reference/<mode>-<requested>-<uuid>/` altında tutulur; JSON NUnit attachment olur ve CI artifact yollarına eklendi. Uzak GitHub Actions işi bu yerel çalışmada tetiklenmedi.

## Yerel doğrulama sonucu

| Kontrol | Sonuç |
|---|---|
| Yeni testler | **32**: 25 dil seçimi/export + 7 Unity profil/loader |
| Tüm solution, Debug | **653/653 geçti**, 8 test projesi; gerçek Unity referansları etkin |
| İlgili Release testleri | **124/124 geçti**, core 117 + IO 3 + GUI 4; gerçek Unity referansları etkin |
| Solution Release build | Başarılı, **0 uyarı / 0 hata** |
| Unity compile matrisi | İki yol × AutoSafe/C# 9 veya explicit C# 7.3; dört birleşim Debug ve Release runner'da geçti |
| Unity referans hash'leri | 20/20 manifest hash'i dosyalardan ayrıca doğrulandı |
| Kanıt dosyaları | Her iki runner'da dört raporun kaynak/DLL/C# hash'leri ve production dil kayıtları bağımsız karşılaştırıldı |
| Kurulum belirtilmeyen kontrol | 2 loader testi geçti; 5 kurulum testi TRX'te **NotExecuted**, CLI'da **Skipped** |
| Unity Editor / native davranış | **NotRun** |

İlgili Release testlerinin geçmesi tüm Release suite'in geçtiği anlamına gelmez; mevcut Debug-assert test sınırı [baseline notlarında](DECOMPILATION_BASELINE.tr.md) kayıtlıdır.

## Açık kapsam

Bu profil CoreModule örneği içindir; tüm Unity API'lerini, üçüncü taraf package/asmdef referanslarını veya bütün Unity 6 sürümlerini kapsamaz. Gerçek eski Mono derleyicisi çıktıları, .NET Framework compatibility profili, tam proje grafiği derlemesi, assembly processor etkileri, MonoBehaviour payload/PPtr değerleri ve Unity Editor import/sahne testleri ayrı kabul kapılarıdır. İlk managed roundtrip'in 20 davranış girdisi ayrı [P07 ilk diliminde](MANAGED_ROUNDTRIP.tr.md) devam eder; bu Unity fixture'ına davranış eşdeğerliği atfedilmez.
