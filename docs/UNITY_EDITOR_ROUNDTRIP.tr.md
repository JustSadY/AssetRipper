# P13 — Çıkarılan Unity projesini Editor'da yeniden açma, ilk dilim

Bu dilim, [gerçek Editor corpus'undan](UNITY_EDITOR_PAYLOAD.tr.md) okunan prefab'ı normal AssetRipper processing/export hattıyla Unity projesine dönüştürür ve **üretilmiş projeyi Unity 6000.4.0f1 Editor'da gerçekten açar**. Decompile edilmiş script'in derlenmesi, script GUID/fileID bağlantısı, seçili alanlar ve PPtr hedefleri Unity'nin kendi okuyucusuyla kontrol edilir. Bu fixture için üretim exporter'ında yeni bir düzeltme gerekmedi; çalışan davranış artık regresyon kapısına bağlıdır.

## Testin izlediği hat

1. P09'un hash-doğrulanmış Linux64 `payload` ve `external` AssetBundle'ları `GameBundle.FromPaths` / `GameAssetFactory` ile yüklenir. Harness manager'ı yalnız kaynak sahibi olduğumuz `Assembly-CSharp` assembly'sini export eder; 20 pinli Unity referansı metadata çözümü için kullanılabilir kalır. Genel oyun/DLL keşif akışının testi değildir.
2. Gerçek `ExportHandler.Process` ve `ExportHandler.Export` çalıştırılır. Assembly processor'ları, prefab/Editor format dönüşümü, script decompilation, YAML/meta ve proje post-exporter'ları etkin kalır. Export başarısı zorunludur.
3. Üretilmiş `ExportedProject` doğrudan Editor'a verilir. Orijinal runtime C# kaynağı kopyalanmaz; orijinal `Assembly-CSharp.dll` dosyasının `Assets` altında bulunmaması ayrıca sınanır. Fixture'ın DLL arşivi `AuxiliaryFiles` altında kalabilir; Unity'nin script derlemesine katılmaz.
4. Yalnız bağımsız bir Editor denetleyicisi `Assets/Editor/AssetRipperValidation` altına eklenir. Bu kod fixture tipine compile-time referans vermez. Editor, AssetRipper'ın decompile ettiği runtime C# dosyalarını kendi compiler'ıyla derler.
5. Batch-mode `AssetRipperRoundTripValidator.Validate` metodu prefab'ları [LoadPrefabContents](https://docs.unity3d.com/6000.4/Documentation/ScriptReference/PrefabUtility.LoadPrefabContents.html) ile açar. [GetMonoBehavioursWithMissingScriptCount](https://docs.unity3d.com/6000.4/Documentation/ScriptReference/GameObjectUtility.GetMonoBehavioursWithMissingScriptCount.html) ve `SerializedObject` üzerinden sonucu bağımsız inceler. Başarılı/başarısız JSON ve buna uygun Editor exit code üretir.

Unity MCP bağlantısı bulunmadığından aynı Editor/derleme doğrulama yaklaşımı batch-mode CLI ile uygulanır. Her vaka yeni bir export projesi kullanır; mevcut proje açılıp değiştirilmez. İlk import sırasında Unity kendi package/Library dosyalarını oluşturabilir. Test, üretilmiş `Packages/manifest.json` dosyasını sadeleştirmez veya kaynak runtime script'iyle değiştirme yapmaz.

## Başarı koşulları

- En az bir çıkarılmış prefab bulunmalı; fixture bileşeninden tam bir tane olmalı ve hiçbir prefab hierarchy'sinde missing-script slot'u bulunmamalı.
- Bileşenin namespace/type/assembly adı beklenen kaynak tipe ait olmalı. `m_Script` gerçekten o bileşenin `MonoScript` asset'ine bağlanmalı; C# asset yolu `EditorPayload.cs` olmalı ve script local ID'si `11500000` olmalı.
- Kalıtılan `inherited=-17`, private SerializeField `hidden=-1234`, flag, UTF-8 string, int dizisi ve nested `count=91` kaynak Editor oracle'ıyla eşleşmeli.
- Beş pointer alanının null/type/name bilgisi korunmalı. Yerel GameObject referansı prefab'ın gerçekten aynı `LocalTarget` child nesnesi olmalı. Harici TextAsset, doğru asset yolu/GUID ve özgün metin içeriğiyle açılmalı. Null alan null kalmalı.
- Editor sürümü oracle ile eşleşmeli; compiler error satırı olmamalı; denetleyici JSON'u bulunmalı ve `Passed=true`, exit code `0` olmalı. Yalnız Editor'ın açılmış veya çıkmış olması başarı sayılmaz.

Kaynak GUID/local ID ile bundle PathID'leri eşit kabul edilmez. Bu kontrol, export sonrası `.meta` GUID'si, prefab script/reference kayıtları ve Unity'nin çözdüğü gerçek hedefler arasındaki zinciri sınar. Denetleyicinin raporu yerel prefab instance'ları için GUID/local ID'yi sıfır gösterebilir; bu transient nesnelerin doğrulaması hierarchy/nesne eşitliğiyle yapılır. Kalıcı MonoScript ve TextAsset kimlikleri ayrıca raporlanır.

## Negatif kontroller

| Vaka | Bilerek yapılan değişiklik | Gerekli sonuç |
|---|---|---|
| TypeTree pozitif | Yok | Editor `Passed=true`, exit `0` |
| Assembly şeması pozitif | TypeTree'siz gerçek bundle, normal export | Aynı alan/ref kontrolü geçer |
| Missing script | Çıkarılmış `EditorPayload.cs.meta` GUID'si değiştirilir | `MissingScript`, exit `1` |
| Değişmiş değer | Prefab'daki `inherited: -17` → `-18` | `ScalarMismatch`, exit `1` |
| Kırık harici ref | Çıkarılmış TextAsset `.meta` GUID'si değiştirilir | `ReferenceMismatch`, exit `1` |

Bozulmalar yalnız yeni test çıktısında, Editor ilk kez açılmadan yapılır. Değiştirilen yol ile önce/sonra SHA-256 kayıtlıdır. Negatif testler yalnız nonzero exit code ile geçmez: compiler error olmaması, gerçek denetleyici JSON'unun bulunması ve doğru hata kategorisinin görülmesi zorunludur. Böylece compiler/başlatma hatası, başarılı bir bozuk-ref tespiti gibi raporlanmaz.

## Çalıştırma

Önce [corpus üretim komutuyla](../TestProjects/MonoBehaviourPayload/README.md) fixture'ı üretin. Ardından:

```sh
ASSETRIPPER_UNITY_EDITOR_EXECUTABLE=/path/to/6000.4.0f1/Editor/Unity \
ASSETRIPPER_UNITY_EDITOR_DATA=/path/to/6000.4.0f1/Editor/Data \
ASSETRIPPER_UNITY_PAYLOAD_FIXTURE=/path/to/generated/fixture \
dotnet test Source/AssetRipper.Tests/AssetRipper.Tests.csproj \
  --filter TestCategory=UnityEditorRoundTrip --logger trx
```

Executable değişkeni yoksa beş test açıkça `Skipped/NotRun` olur. Değişken verilmişse executable, corpus ve pinli referanslar geçerli olmalıdır; yapılandırma hatası başarı veya skip sayılmaz. Kullanılabilir Editor lisansı ve export'un package bağımlılıkları gerekir. Testler birbiriyle paralel Editor başlatmaz; her Editor çalışmasına üç dakika sınır konur. Süre aşımında testin başlattığı process tree durdurulur.

Standart CI Editor/lisans/corpus kurmaz; atlanan kontroller tamamlanmış kabul edilmez. Uzak GitHub Actions çalışması bu yerel doğrulamada tetiklenmedi. Bu test, kaynak sahibi olduğumuz fixture'ın script'lerini Editor'da yükler; genel kullanıcı oyun kodunu SystemTester'da yürütme özelliği eklemez.

## Kanıt ve çıktı

`TestResults/unity-editor-roundtrip/<schema>-<damage>-<uuid>/` altında:

- `ExportedProject`: gerçekten açılan proje; kaynak script, YAML, `.meta`, Packages ve ProjectSettings korunur. Başarılı veya beklenen negatif vakadan sonra geçici `Library` cache'i kaldırılır.
- `editor-import-report.json`: Editor'ın gözlemleri, script GUID/local ID, beş ref, missing-script/component sayıları ve hata kategorileri.
- `editor-run.json`: process exit code ve compiler error özeti. Tam Editor/stdout/stderr log'ları ayrı yerel geçici dizindedir; rapor bu dizini gösterir.
- `roundtrip-inputs.json`: şema/vaka, export sonucu, mutation hash'leri ve import öncesi proje dosyalarının SHA-256 listesi.
- `Evidence/BeforeImport`: denetleyici dahil Editor'a verilen Assets/Packages/ProjectSettings snapshot'ı; `Evidence/Input`: 18 özgün corpus girdisi. Oracle ve corpus manifesti kökte de durur.

CI artifact yolları yalnız kökteki JSON'ları ve `Evidence` snapshot'larını alır. `Library`, Editor çalışma cache'leri ve tam log'lar artifact'a eklenmez. Denetleyici JSON'u NUnit attachment'tır. Kaynak code/manifest ve snapshot hash'leri, Editor'ın açtığı dosyaların ne olduğunu incelemeyi sağlar.

## Yerel doğrulama sonucu

| Kontrol | Sonuç |
|---|---|
| Yeni Editor roundtrip testleri | **5/5 geçti**, Debug ve Release runner'larında |
| İki pozitif vaka | TypeTree ve gerçek assembly şeması: Editor exit `0`, missing script `0`, bileşen `1`, alan/ref kontrolleri geçti |
| Üç negatif vaka | Missing script, değişmiş scalar ve kırık harici ref doğru hata kategorisiyle exit `1` verdi; compiler error veya eksik rapor başarı sayılmadı |
| Tüm solution, Debug | **712/712 geçti**, 8 test projesi; önceki Unity referans/corpus testleri de etkin |
| İlgili Release testleri | **183/183 geçti**, core 176 + IO 3 + GUI 4 |
| Solution Release build | **0 uyarı / 0 hata** |
| Bağımsız kanıt denetimi | On vaka raporunun input/snapshot hash'leri, mutation hash'leri ve exit/status sonuçları doğrulandı; pozitif vakalarda script/meta/YAML ve TextAsset GUID/fileID, int dizisinin YAML byte içeriği ve beş ref ayrıca karşılaştırıldı |
| Editor executable yapılandırılmayan kontrol | **5/5 `Skipped`**, tekil TRX kayıtlarında **NotExecuted** |
| Play Mode / sahne bundle'ı / IL2CPP | **NotRun** |

Bu ortamda build/test için `--no-restore -p:AllowMissingPrunePackageData=true` kullanıldı; paket veya SDK sürümleri değiştirilmedi. İlgili Release sonuçları tüm Release suite'in geçtiği anlamına gelmez; önceden doğrulanmış Debug-assert test sınırı [baseline notlarında](DECOMPILATION_BASELINE.tr.md) durur. Uzak CI çalıştırılmadı.

## Açık kapsam

Bu ilk P13 kapısı **6000.4.0f1 / Linux64 / tek MonoBehaviour prefab fixture'ı** içindir. Gerçek serialize edilmiş sahne bundle'ı, çoklu sahne/prefab varyantları, eski Mono/compiler profilleri, asmdef/üçüncü taraf package grafikleri, managed-reference registry kimlikleri, IL2CPP ve Play Mode davranışı açık kalır. Seçili fixture'ın Editor'da doğru açılması bütün oyunların kayıpsız geri getirildiği anlamına gelmez.
