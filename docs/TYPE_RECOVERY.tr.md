# P08 — Aynı C# dosyasındaki bağımsız tipleri kurtarma

Bu ilk dilim, tek bir bozuk tipin aynı çıktı dosyasındaki sağlam tipleri kaybettirdiği somut bir hatayı düzeltir. Ortak `ScriptDecompilation` hattında çalışır; Unity-project ve primary-content export yolları aynı kurtarmayı ve rapor sözleşmesini kullanır.

## Hatanın kanıtı

Pinli `AssetRipper.ICSharpCode.Decompiler 11.1.0.9736`, dosyaları zaten birbirinden bağımsız işler; çoğu metot gövdesi hatasında da kısmi kaynak bırakır. Bu davranışın üzerine bütün assembly'yi körlemesine tekrar denemek eklenmedi.

ILSpy, generic arity'yi dosya adından çıkarır ve dosya gruplarını büyük/küçük harfe duyarsız oluşturur. Örneğin `SharedName` ve `SharedName<T>` aynı `SharedName.cs` dosyasına gider. Kaynak sahibi olduğumuz fixture'da generic tipin `DecimalConstantAttribute` scale değeri AsmResolver ile geçersiz `255` yapılır. Field decompilation hatası dosyanın tamamını ILSpy hata yorumuyla değiştirir; sağlam `SharedName`, state tutan `Add` metodu ve `Nested` tipi de kaybolur. Başka dosyadaki `Independent` etkilenmez.

Düzeltme öncesi regresyon testi sağlam `Add` metodunu bulamadığı için başarısız oldu. Kurtarma sonrasında sağlam tip ve nested tipi yeniden derlenebilir kaynak olarak çıkar; bozuk generic tip başarısız olarak raporda kalır. Kullanılan upstream dosya/gruplama davranışı [pinli WholeProjectDecompiler kaynağında](https://github.com/AssetRipper/ILSpy/blob/c9f90082e63c846d974349831881d90d79741c98/ICSharpCode.Decompiler/CSharp/ProjectDecompiler/WholeProjectDecompiler.cs) görülebilir.

## Kurtarma sınırı

1. Normal whole-project decompilation tamamlanır ve ILSpy'ın kaydettiği hatalar incelenir. Fatal hatalar kurtarmadan önce dışarı taşınır.
2. Aynı sanitize edilmiş C# yoluna giden birden fazla dahil edilmiş top-level tipi olan gruplar bulunur. Yalnız `// ILSpy could not decompile this.` ile başlayan ve tamamen yorum/boş satırlardan oluşan dosyalar adaydır. Mevcut gerçek kısmi C# dosyaları korunur.
3. Her özgün top-level tip, nested tipleriyle birlikte bir kez ayrı decompile edilir. Aynı resolver, dil/format ayarları ve `CreateDecompiler` transform'ları kullanılır. Denemeler seri çalışır; tek lazy type system paylaşılır. Generic parametre sayısı ve metadata token sıralaması kullanılır.
4. Decompiler hatası olmayan ilk tip özgün C# yoluna yazılır; böylece nongeneric `MonoScript` için canonical yol korunur. Diğer sağlam tipler `<ad>.Recovered-<token>[-sıra].cs` dosyalarına yazılır. Var olan ek dosya/dizin adları atlanır.
5. İlk değiştirmeden önce özgün hata dosyasının **tam baytları** `_DecompilationEvidence/<yol-hash>[-sıra].txt` altında korunur. İçerik SHA-256 özeti rapora yazılır. `.txt` kanıt dosyası SDK'nın C# glob'una girmez. Mevcut kanıt dosyaları ezilmez; kanıt diziniyle çakışan kullanıcı dosyası silinmez, IO hatası dışarı taşınır.
6. Her tip başarısızsa özgün hata dosyası değişmez. Yeni metot stub'ı, sahte gövde veya tip yeniden adlandırması üretilmez.

`.g.resources` içeren assembly'lerde ILSpy'ın resource-backed partial-type / companion-file haritası public API'de bulunmadığından aday grup açık `SkipReason` ile atlanır. Bu konservatif sınır bütün bu assembly'lere uygulanır. Bu dilim WPF/BAML kurtarması sağlamaz; resource testi geçerli boş container ile yalnız skip dalını sınar. Upstream resource writer'ın fiziksel dosya sistemi bağımlılığı burada değiştirilmedi.

## Üretim raporu

`script-export-report.json` schema 1'e ek alanlar gelir. Assembly kaydının `TypeRecoveries` listesinde her dosya için şunlar vardır:

| Alan | Anlam |
|---|---|
| `OriginalFile` | Assembly kaynak çıktı dizinine göre özgün C# yolu |
| `EvidenceFile` / `EvidenceSha256` | Korunan hata dosyasının göreli yolu / tam bayt hash'i; değiştirme denenmediyse null |
| `Types` | Tipin metadata adı, `0x02...` token'ı, `Recovered` / `Failed`, göreli çıktı yolu ve deneme diagnostics'leri |
| `SkipReason` | Companion map gibi bir sınır nedeniyle denenmeyen grubun gerekçesi |
| `RecoveredTypeCount` | Bu grupta decompiler hatası olmadan yazılan top-level tip sayısı |

Assembly kaydında da toplam `RecoveredTypeCount` bulunur. Nested tipler ayrı başarı sayısına eklenmez. Diagnostics kayıtlarına `Stage`, `MetadataToken` ve `ContainingType` eklenir: normal hata `WholeProject`, deneme hatası `TypeRecovery`, yazma hatası `TypeRecoveryWrite` olur. Field token'ı `0x04...` veya method token'ı `0x06...`, onu içeren tipin metadata adıyla ilişkilendirilir; token'lar yalnız kendi modüllerinde anlamlıdır. `FileName`, ILSpy'ın assembly dosya bilgisidir; C# çıktı yolu olarak yorumlanmamalıdır.

**Özgün hatalar silinmez; kısmi kurtarma genel `Succeeded` sonucunu true yapmaz.** `Recovered`, yalnız decompiler hatası olmadan kaynak yazıldığını ifade eder. Ürün export'u otomatik compiler/behavior doğrulaması yapmaz. Çözümlenemeyen dependency hataları da genel sonucu başarısız tutar.

İptal, bellek tükenmesi, yetki ve dosya sistemi hataları, iç içe veya aggregate exception içindeyken de dışarı taşınır. Kurtarılabilir bir tip/yazıcı hatası sonraki sibling denemesini engellemez. Dosya yazımı transaction değildir; yazma ortasında hata olursa kısmi dosya kalabilir, özgün hata kanıtı önceden saklanmıştır. Mevcut koleksiyon hata sınırları geçerlidir.

## Test ve kanıt dosyaları

`ScriptTypeRecovery` kategorisi şu durumları kapsar:

- Nested/flat namespace yolları ve fully-qualified ayarla gerçek bozuk metadata; sağlam root/nested tiplerin korunması, field-token ilişkisi ve hedef C# 9 altında yeniden derleme.
- İki gerçek export giriş noktası; production JSON sonucu başarısız kalırken kaynakların derlenmesi, özgün bozuk DLL arşiv baytlarının korunması ve Unity canonical `.cs.meta` dosyası.
- Kaynak sahibi olduğumuz özgün fixture ve tekrar derlenen çıktı için beş bilinen gözlem: ardışık `Add(3)`, `Add(-1)`, state alanı, nested hesaplama ve bağımsız tip. Beklenen sonuç **`[3, 2, 2, 15, 20]`** hem özgün kod hem aday için elle yazılmış oracle ile kontrol edilir.
- Sağlam assembly'nin tekrar denenmemesi; birden fazla sağlam sibling ve önceden bulunan ek dosyanın korunması; bütün tiplerin başarısız olması; mevcut kısmi method-error kaynağının değiştirilmemesi; resource skip gerekçesi.
- Kanıt diziniyle çakışan dosyanın korunması; IO/cancel/access/OOM/wrapped fatal hataların taşınması; kurtarılabilir yazıcı hatasından sonra başka sibling'in devamı.

Fixture, P07'nin hash-pinned **ControlledFixture.Net100** referans setini ve C# 9'u kullanır. Bozuk metadata ayrı DLL'de üretilir; davranış için yalnız kaynak sahibi olduğumuz özgün/tekrar derlenmiş fixture yürütülür. Oyun DLL'leri veya bozuk generic tip yürütülmez. Bu örnek gerçek eski Mono compiler çıktısı, Unity API profili veya IL2CPP değildir.

```sh
dotnet test Source/AssetRipper.Tests/AssetRipper.Tests.csproj \
  --filter TestCategory=ScriptTypeRecovery --logger trx
```

Kanıtlar test output dizininde `TestResults/type-recovery/<Primary|Unity>-<uuid>/` altında tutulur: özgün kaynak/DLL, bozuk girdi DLL'si, tekrar derlenen DLL, bütün çıkarılan kaynak/yan dosyalar, üretim script raporu ve `type-recovery-verification.json`. Rapor; referans profili, compile diagnostics, DLL hash'leri ve davranış dizilerini içerir. `AssemblyRecoveryComplete=false`, `UnityEditorValidation=NotRun` açıkça kalır. JSON NUnit attachment'tır; bütün dizin CI artifact yoluna eklendi. Uzak GitHub Actions işi bu yerel doğrulamada tetiklenmedi.

## Yerel doğrulama sonucu

| Kontrol | Sonuç |
|---|---|
| Yeni tip kurtarma testleri | **17/17 geçti**, Debug ve Release runner'larında |
| Tüm solution, Debug | **670/670 geçti**, 8 test projesi; önceki gerçek Unity referans profili etkin |
| İlgili Release testleri | **141/141 geçti**, core 134 + IO 3 + GUI 4 |
| Solution Release build | Başarılı; son incremental derleme **0 uyarı / 0 hata** |
| İki export yolunun çıktıları | C# 9 / ControlledFixture.Net100 ile yeniden derlendi; her biri 5/5 bilinen davranış gözlemi geçti |
| Kanıt denetimi | Debug/Release × iki yolun DLL/kanıt hash'leri, göreli yolları ve genel başarısızlık bayrakları ayrıca doğrulandı |
| Unity Editor / IL2CPP recovery | **NotRun** |

Bu ortamda derleme/test için `--no-restore -p:AllowMissingPrunePackageData=true` kullanıldı; SDK veya paket sürümleri değiştirilmedi. İlk geniş Release derlemesi değiştirilmeyen DocExtraction JSON generator, Sprite ve Animator dosyalarından 12 uyarı verdi; sonraki incremental derleme sıfır uyarıyla tamamlandı. Tüm Release suite'in geçtiği iddia edilmiyor; önceki Debug-assert test sınırı [baseline notlarında](DECOMPILATION_BASELINE.tr.md) kayıtlıdır. Gerçek Unity referanslarının etkin olması bu yeni .NET fixture'ının Unity profiliyle doğrulandığı anlamına gelmez.

## Açık kapsam

Bu ilk dilim yalnız kanıtlanmış paylaşılan-dosya kaybını giderir. Tek tipli dosyalar, assembly/type-system kurulumu sırasında global metadata hatası, nested tipi bağımsız kurtarma, member düzeyinde yeni fallback, üretimde compile doğrulaması, bütün proje grafiğinin derlenmesi ve Unity Editor import/sahne/PPtr kontrolleri ayrı kabul kapılarıdır. Kısmi kaynak üretmiş metot hatalarında ILSpy'ın mevcut çıktısı korunur; eksik gövdenin doğru kurtarıldığı iddia edilmez. Native/IL2CPP decompilation ve obfuscation çözümü eklenmedi.
