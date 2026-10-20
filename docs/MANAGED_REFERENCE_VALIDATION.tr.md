# Managed-reference tablosunda yanlış başarıyı önleme

Bu P09 dilimi `[SerializeReference]` registry **format 2** için nesne kimliği tutarlılığını doğrular. Bütün baytların okunması, grafiğin doğru olduğu anlamına gelmez: aynı RID ile iki nesne veya tabloda bulunmayan bir RID'ye bağlanan alan, okuma tamamlandığı halde veri kaybettirebilir.

## Üretim davranışı

`SerializableStructure.Read`, host'un registry ve alanlarını okuduktan sonra `ManagedReferenceGraphValidator` çağırır. Kontrol:

- Tek host tablosundaki **0 ve üzeri RID'lerin tekrarını** reddeder.
- Root, list/array, inline nesne ve registry verisi içindeki managed-reference alanlarının aynı host tablosunda karşılığını arar. Hata mesajı RID ve `references.RefIds[1].data.next` gibi alan yolunu içerir.
- Önce tüm RID kümesini oluşturur; ileri referanslar ve döngüler geçerlidir. Mantıksal RID kenarlarını recursive takip etmez, yalnız fiziksel okunmuş alanları tarar.
- Kimlik kümesini her host için yeniden oluşturur. Başka host'ta aynı RID bulunması mevcut host'taki eksik kaydı tamamlamaz; farklı host'ların aynı RID'leri kullanması geçerlidir.
- Var olan **-2 null / -1 bilinmeyen** işaretçi davranışını korur. RID **0**, 64 bit kimlikler ve boş namespace geçerlidir.

`ManagedReferenceRegistryReader`, format 2'de 0 ve üzeri RID taşıyan kaydın sınıf veya assembly adı boşsa hata verir. Önceki `className.Length == 0 || assemblyName.Length == 0` yolu bu kaydı da boş/null veri gibi okuyabiliyordu. Namespace boşluğu aynı kurala dahil edilmez. Normal adı bulunan fakat çözülemeyen türün mevcut hata davranışı korunur.

Unity'nin [RID atama sözleşmesi](https://docs.unity3d.com/kr/current/ScriptReference/Serialization.ManagedReferenceUtility.SetManagedReferenceIdForObject.html), kimliklerin tek host içinde benzersiz olduğunu ve farklı host'larda tekrar kullanılabileceğini belirtir. [Null ve bilinmeyen işaretçileri](https://docs.unity3d.com/ja/current/ScriptReference/Serialization.ManagedReferenceUtility.html) ayrı değerlerdir. Eksik tür halinde [Unity'nin veriyi saklama davranışı](https://docs.unity3d.com/kr/current/ScriptReference/SerializationUtility.HasManagedReferencesWithMissingTypes.html), eksik bir türü veri içermeyen null kaydı saymamak için ek gerekçedir; bu dilim eksik türü otomatik geri adlandırmaz veya yeniden oluşturmaz.

## Hatalı nesne korunur ve başarısızlık görünür olur

Yeni hatalar mevcut `TryRead` / `UnreadableStructure` hattına girer. Ham script segmenti, header'a göre konumu, tüketilen/toplam bayt sayısı, şema kaynağı ve diagnostic korunur. Kısmen okunmuş alanlar doğru veri gibi yayımlanmaz; dependency kapsamı **Incomplete** olur. Deferred raw snapshot durumu **Partial** olur; tekrarlı okuma aynı journal olayını yeniden yazmaz.

İki export yolu da `collection-export-report.json / MonoBehaviourFailures` ile genel başarısızlığı bildirir. Ham koruma opt-in kapalıyken bile `.bin` fallback'i SHA-256 ile saklanır. Bağımsız TextAsset export'u devam eder. Bu sonuç geçerli bir Unity projesi/Editor import'u diye sunulmaz.

## İki ayrı fixture katmanı

`TestCategory=ManagedReferenceValidation` toplam **32** vakadır:

| Katman | Vaka | Kanıt |
|---|---:|---|
| Kontrollü C# shim + bağımsız binary writer | 9 | Duplicate RID; root/array/inline/node dangling RID; -3 alan RID; bulunamayan tür; boş sınıf/assembly adı. Ham koruma ve Partial durumu |
| Kontrollü pozitif okuma | 6 | Little/big endian, ileri/geri kenarlar, null/bilinmeyen sentinel, global namespace, RID 0 |
| Kontrollü big-endian bozuk girdi | 2 | Duplicate ve registry düğümündeki dangling RID |
| Kontrollü host scope | 1 | İki host'ta aynı kimlikler geçer; üçüncü host diğerinin tablosunu kullanamaz |
| Kontrollü iki exporter | 2 | Yanlış başarı yok, ham bayt/hash aynı, sağlam TextAsset devam eder |
| Gerçek Editor nesnesinin kopyasında mutation | 8 | İki şema × duplicate/root dangling/node dangling/missing type |
| Gerçek bundle + processing + iki exporter | 4 | İki şema × Unity-project/primary-content; duplicate kayıt ve bağımsız harici TextAsset |

Kontrollü örneğin DLL'si test host'unda yürütülmez. Bu katman kendi başına gerçek Unity çıktısı diye adlandırılmaz. Düzeltme öncesi 16 kontrollü vakadan **11'i**, gerçek Editor girdisindeki 8 vakadan **6'sı** yanlış başarıyı gösterdi. Bulunamayan tür için mevcut koruma yolu zaten geçiyordu. İlk RID kontrolüne eklenen iki boş tür kaydı testi de önce başarısız oldu; reader guard'ı bu açığı kapattı.

Gerçek katman, önceki kaynak sahibi olduğumuz **Unity 6000.4.0f1 / Linux64** graph corpus'unu kullanır. `ManagedReferenceMutationFactory` yalnız factory'ye verilen **nesne kopyasını** değiştirir. Header ve fixture'ın bilinen binary düzeni sırayla okunarak offset hesaplanır; byte araması veya özgün bundle'a yazma yapılmaz. Bir RID'nin sekiz baytı veya `NumberNode` → `AbsentNode` için aynı uzunlukta on bayt değiştirilir. İki TypeTree modu, gerçek DLL ve pinli Unity metadata referansları kullanılır.

## Çalıştırma ve kanıt

```bash
ASSETRIPPER_UNITY_EDITOR_DATA=/path/to/6000.4.0f1/Editor/Data \
ASSETRIPPER_UNITY_MANAGED_REFERENCE_FIXTURE=/absolute/graph-corpus \
dotnet test Source/AssetRipper.Tests --no-restore \
  -p:AllowMissingPrunePackageData=true \
  --filter TestCategory=ManagedReferenceValidation --logger trx
```

Corpus üretimi önceki [managed-reference sözleşmesindeki](UNITY_MANAGED_REFERENCES.tr.md) `--managed-references` komutuyla yapılır. Bu yeni kategoride Editor executable gerekli değildir; var olan gerçek nesneler statik okunur. Corpus yoksa **20** kontrollü vaka çalışır, **12** gerçek vaka `Skipped/NotRun` olur. Yapılandırılmış bozuk corpus veya eksik/yanlış hash'li referanslar skip sayılmaz.

Kanıtlar `TestResults/unity-managed-reference/<schema>-Invalid-<kind>-<damage>-<id>/` içindedir:

- `fixture-manifest.json`, oracle ve `Evidence/Input/` altında 20 değişmemiş corpus girdisi;
- `Evidence/Mutation/original-object.bin` ve `modified-object.bin`;
- `invalid-graph-verification.json` içinde iki nesne hash'i, mutation offset/uzunluğu, script segmenti başlangıcı, şema ve sonuç;
- `collection-export-report.json` ve `Evidence/MonoBehaviourRecovery/` altında hash'li başarısız segment.

Mevcut CI artifact yolları JSON ve `Evidence` dizinlerini kapsar; yeni bir Unity indirme veya rastgele oyun kodu çalıştırma adımı eklenmedi. Sağlam prefab'ların gerçek Editor reimport kontrolleri, ayrı mevcut kategorilerle regresyon olarak tekrar çalıştırılır.

## Yerel doğrulama

| Kontrol | Sonuç |
|---|---|
| Tüm solution, Debug | **758/758 geçti**, 8 test projesi; gerçek corpus ve Editor kapıları etkin |
| İlgili Release seti | **229/229 geçti**: son core koşusu 222 + IO 3 + GUI 4 |
| Yeni kategori | **32/32 geçti**, Debug ve Release |
| Corpus/Editor değişkenleri olmadan | **20 geçti, 12 NotExecuted**; tekil TRX kayıtları doğrulandı |
| Release build | Başarılı, **0 hata / 1 mevcut Animator TODO uyarısı** |
| Bağımsız kanıt denetimi | 24 yeni gerçek-girdi raporunda **552** corpus/nesne/segment hash'i, mutation sınırları ve recovery segmentleri eşleşti; özgün corpus'un 20 hash'i değişmedi |

İlk Release core koşusunda **221 geçti / 1 başarısız** sonucu vardı: mevcut negatif döngü testindeki Unity Editor, checker başlamadan native named-pipe `ToFileDescriptor` assert'iyle exit **134** verdi ve sonuç raporu yazmadı. Bu başarısızlık başarıya çevrilmedi, otomatik retry eklenmedi. Aynı proje, 12 C#/prefab dosyasının hash'leri değiştirilmeden yeniden açıldığında beklenen `Cycle`/`NodeData` negatif raporunu ve exit **1** üretti. Aynı derlenmiş kodla bütün ilgili core seti tekrar çalıştırıldı ve **222/222** geçti. İlk başarısız TRX, Editor run kaydı ve aynı projeye ait ayrı retry raporu korundu. Tam Release suite'in önceden bilinen Debug.Assert bağımlı testi bu ilgili sete dahil değildir. Uzak CI çalıştırılmadı.

TRX dizinleri `/tmp/assetripper-ref-validation-debug-results/`, `/tmp/assetripper-ref-validation-release-results/` (ilk koşu ve IO/GUI), `/tmp/assetripper-ref-validation-release-clean-results/` (son core) ve `/tmp/assetripper-ref-validation-unconfigured-results/` altındadır. Geçici dizinler kalıcı teslim değildir; corpus ve test artifact kanıtları yukarıdaki sözleşmeye göre korunur.

## Sınırlar

Format **1** için yeni kurallar uygulanmaz; eski index/null sözleşmesi bu dilimde doğrulanmış sayılmaz. Duplicate negatif sentinel kayıtlarını ayıklama, bütün olası TypeTree şekilleri, büyük grafik performansı, namespace/type rename recovery, eksik türlerin alan bazında kısmi kurtarılması, SerializedFile 23'ün tam matrisi, sahne bundle'ı, Play Mode ve IL2CPP açık kalır. Bu kontrol, çözümlenmiş alanlardaki kimlik tutarlılığıdır; kullanılan şemanın bütün alanlarda doğru olduğuna dair genel garanti değildir.
