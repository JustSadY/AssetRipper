# Gerçek Unity managed-reference grafiği ve klon veri kaybı

Bu dilim Unity **6000.4.0f1 / StandaloneLinux64** üzerinde kaynak sahibi olduğumuz bir MonoBehaviour prefab'ını kapsar. `[SerializeReference]` için mevcut registry okuyucusu gerçek bundle'larla ölçülür; klonlamada bulunan somut veri kaybı düzeltilir. Her Unity sürümü veya bütün grafik şekilleri için genel destek iddiası değildir.

## Bulunan ve düzeltilen hata

`ReferencedObject.data` şeması başlangıçta alanı olmayan `ReferencedObjectData` yer tutucusudur. Okuyucu gerçek şemayı hemen önceki `type` kaydından çözüp bu alana koyuyordu. `SerializableValue.CopyValues` ise klon yaratırken tekrar boş yer tutucuyu kullanıyordu. RID/type kayıtları durmasına rağmen `label`, `next`, `target` ve alt türe ait alanlar kayboluyordu; dependency listesi de eksiliyordu.

Gerçek Editor corpus'uyla TypeTree ve assembly yollarındaki iki klon kontrolü düzeltmeden önce **0/2** geçti: beklenen dört alan yerine sıfır alan gözlendi. Kopyalama artık yalnız bu dinamik `data` alanında çözümlenmiş `SerializableStructure`'ı kendi gerçek şemasıyla deep-clone ediyor. İç PPtr'ler aynı converter üzerinden hedef koleksiyona çevriliyor. Boş/null kayıt boş kalıyor. Diğer alanların şema dönüştürme davranışı değiştirilmedi.

Editor gerektirmeyen üç regresyon; iki düğümün verilerini ve 64 bit ileri/geri RID'lerini, klon değişikliğinin özgün alanı değiştirmemesini, aynı/farklı koleksiyonlarda PPtr hedefini ve null kaydın bağımsız kopyasını kontrol eder. Bunlar varsayılan CI'da da çalışır.

## Kaynak corpus ve bağımsız doğrulama

`TestProjects/MonoBehaviourPayload` içindeki `GraphPayload.cs` ve `GraphFixtureBuilder.cs` şu grafiği oluşturur:

- Kalıtılmış `root` ve `alias` aynı `NumberNode` nesnesidir; RID **101**.
- `TextNode` RID **4294967419** ile 32 bit sınırını aşar. İlk düğüm ikinciye, ikinci düğüm ilkine bağlanır.
- `[SerializeReference] List<GraphNode>` ikinci düğümü, null'u ve ilk düğümü içerir. Inline sınıf içindeki `child` ikinci düğümü paylaşır. Ayrı `missing` alanı null'dur; RID **-2**.
- Polimorfik alanlar `number=-37`, `text="polymorphic ☃"`; iki düğümde Unicode/string alanları vardır.
- Registry verisindeki Unity nesne referanslarından biri prefab içindeki `GraphLocal`, diğeri ayrı bundle'daki `GraphExternal` TextAsset'ıdır.

Builder sabitleri ve gerçek `ReferenceEquals` ilişkilerini kontrol eder. Sahne kaydedilip yeniden açılır; prefab asset'ı da okunur. `SerializedProperty.managedReferenceId` ve `ManagedReferenceUtility` ile gözlenen yedi RID, iki somut düğüm, nesne alanları ve iki Unity hedefi `oracle.json` içine yazılır. Editor API sözleşmesi için [managed-reference yardımcıları](https://docs.unity3d.com/ja/current/ScriptReference/Serialization.ManagedReferenceUtility.html) ve [SerializedProperty RID erişimi](https://docs.unity3d.com/ja/current/ScriptReference/SerializedProperty-managedReferenceId.html) kullanılır; asıl sürüm kanıtı çalıştırılan 6000.4.0f1 Editor'dır.

Aynı prefab/harici asset map'i TypeTree açık ve `DisableWriteTypeTree` ile ayrı bundle'lara dönüştürülür. Corpus, Editor'ın derlediği `Assembly-CSharp.dll`, oracle, prefab/sahne kaynakları ve üretici kodun tamamını SHA-256 manifestinde saklar. Bu çalıştırmadaki corpus **20** hash'li dosyadır. Üretim temiz/geçici projede yapılır; önceki corpus dizini boşaltılmaz.

## Test kapıları

`TestCategory=UnityManagedReference` içinde **14** vaka vardır:

| Kapı | Vaka | Kontrol |
|---|---:|---|
| Editor gerektirmeyen klon regresyonu | 3 | Dinamik şema, veri, RID, PPtr dönüşümü, bağımsız/null kopya |
| Gerçek bundle okuma | 4 | İki şema × harici bundle var/yok; yedi RID, registry null kaydı, iki somut düğüm ve dependency yolları |
| Gerçek bundle deep-clone | 2 | İki şemada gerçek düğüm verileri/dependency'ler ve özgün nesneden bağımsızlık |
| Export sonrası Editor import | 2 | İki şemada decompile edilmiş C# derlemesi; script bağı, kimlik paylaşımı, döngü, düğüm/PPtr verileri |
| Bozulmuş export için Editor negatif kontrolleri | 3 | Alias RID değiştirme, döngünün geri kenarını null yapma, `number` değerini değiştirme |

Eksik harici bundle testinde ham PPtr FileID/PathID'leri tam yükleme ile aynı kalmalıdır; eksik hedef çözümlenmiş sayılmaz. Grafik dependency'leri registry üzerinden yalnız iki fiziksel Unity hedefi olarak bulunur; döngü kenarları tekrarlı recursive traversal'a dönüştürülmez.

Editor roundtrip, normal `ExportHandler.Process/Export` çıktısını açar. Özgün runtime DLL veya fixture runtime kaynakları çıkarılan projenin `Assets` dizinine eklenmez. Bağımsız Editor checker, çıkarılan türleri reflection ile okur; canlı nesnelerin `ReferenceEquals` ilişkisini ve iki yöndeki döngüyü sonlu sayıda adımla denetler. Ayrıca script GUID/local ID bağını, missing-script sayısını, RID'leri, türe özgü alanları ve gerçek yerel/harici Unity hedeflerini kontrol eder.

Negatif kontrollerde gerçek JSON raporu, compiler hatası olmaması ve exit **1** gerekir. Alias bozulması `SharedIdentity`, geri kenar bozulması `Cycle`, alan değişikliği `NodeData` üretmelidir; validation exception veya missing script bu üç kapı için kabul edilmez.

## Çalıştırma

Önce kaynak sahibi olduğumuz örneği kurulu Editor ile üretin:

```bash
python3 TestProjects/build_payload_fixture.py \
  --unity /path/to/6000.4.0f1/Editor/Unity \
  --output /absolute/new/graph-corpus \
  --managed-references
```

Sonra mevcut pinli Unity referans profiliyle test edin:

```bash
ASSETRIPPER_UNITY_EDITOR_EXECUTABLE=/path/to/6000.4.0f1/Editor/Unity \
ASSETRIPPER_UNITY_EDITOR_DATA=/path/to/6000.4.0f1/Editor/Data \
ASSETRIPPER_UNITY_MANAGED_REFERENCE_FIXTURE=/absolute/new/graph-corpus \
dotnet test Source/AssetRipper.Tests --no-restore \
  -p:AllowMissingPrunePackageData=true \
  --filter TestCategory=UnityManagedReference --logger trx
```

`AllowMissingPrunePackageData` bu çalışma ortamının SDK/package prune verisi uyumsuzluğu için kullanıldı; referans hash doğrulamasını kapatmaz. Assembly şeması .NET host assembly'lerine fallback yapmaz ve özgün fixture DLL'sini test host'unda yürütmez. Kurulumdaki 20 pinli referans image'ı önce doğrulanır.

Corpus yapılandırılmadığında altı gerçek bundle kontrolü, corpus veya executable yapılandırılmadığında beş Editor import kontrolü `Skipped/NotRun` olur. Üç bağımsız regresyon çalışmaya devam eder. Yapılandırılmış yanlış corpus/hash/Editor yolu veya eksik referanslar test hatasıdır; otomatik skip'e çevrilmez. Eski `ASSETRIPPER_UNITY_PAYLOAD_FIXTURE` ayrı kalır.

## Kanıtlar ve açık sınırlar

| Yerel doğrulama | Sonuç |
|---|---|
| Yeni kategori, gerçek corpus ve Editor etkin | **14/14 geçti**, Debug ve Release |
| Tüm solution, Debug | **726/726 geçti**, 8 test projesi; iki corpus/Editor etkin |
| İlgili Release testleri | **197/197 geçti**, core 190 + IO 3 + GUI 4 |
| Solution Release build | **0 uyarı / 0 hata** |
| Unity/corpus ayarları olmadan | **3 geçti, 11 NotExecuted**; tekil TRX sonuçları doğrulandı |
| Varsayılan üreticiye geri uyumluluk | Yeni **20 dosyalı** scalar/PPtr corpus'u üretildi; mevcut iki kategori **14/14** geçti; bütün girdi hash'leri ayrıca eşleşti |
| Bağımsız kanıt denetimi | İki konfigürasyondaki 10 Editor + 8 okuma raporu, **550** corpus/snapshot hash'i, üç mutation'ın özgün/değişmiş YAML hash'leri ve pozitif script GUID/fileID bağları eşleşti |

Tam Release suite'in daha önce temiz HEAD'de de gösterilen Debug.Assert bağımlı `ExportIdHandlerTests` davranışı bu sonuca dahil edilmez; burada ilgili Release seti raporlanır. Uzak CI tetiklenmedi.

Yerel grafik corpus'u `Source/0Bins/TestCorpus/UnityGraph6000.4-20261020-d82fb013/`, yeni scalar/PPtr üretimi `Source/0Bins/TestCorpus/UnityPayload6000.4-20261020-e8b6c04a/` altında korunur. İlk corpus ve mevcut kullanıcı dosyaları temizlenmedi. Düzeltme öncesi regresyon sonucu `/tmp/assetripper-graph-initial-results/`, son Debug ve Release TRX'leri `/tmp/assetripper-graph-debug-results/` ve `/tmp/assetripper-graph-release-results/` içindedir; geçici dizinler kalıcı teslim sayılmaz.

`Source/0Bins/Other/AssetRipper.Tests/<Configuration>/TestResults/unity-managed-reference/` altında okuma raporları, Editor JSON sonuçları, mutation/hash kayıtları, corpus girdileri ve import öncesi proje snapshot'ı saklanır. CI yalnız JSON ve `Evidence` dizinlerini yükler. Geçici `Library` cache'leri beklenen test sonucu sonrası silinir; tam Editor log'ları yerel geçici dizinde kalır. Corpus ve kurulu Unity dosyaları depoya eklenmez.

Bu dilim registry format **2** ve mevcut iki şema çözümleme yoluyla sınırlıdır. Eski format **1**, eksik/yeniden adlandırılmış managed türler, duplicate/dangling RID tespiti, farklı host'lar arası RID scope'u, geniş grafik/sürüm/platform matrisi, sahne bundle'ı, Play Mode ve IL2CPP kabul kapıları açık kalır. Ürüne rastgele oyun kodu çalıştıran bir mekanizma eklenmedi.

Sonraki [RID/tür tutarlılığı dilimi](MANAGED_REFERENCE_VALIDATION.tr.md), format 2 için duplicate/dangling RID ve eksik tür metadata'sını artık hata olarak bildirir; host scope'unu ve ham koruma/export başarısızlığı sözleşmesini test eder. Önceki tablodaki sonuçlar bu ilk klon/Editor paketinin tarihsel ölçümüdür; eksik türü otomatik geri oluşturma ve format 1 hâlâ açık kalır.
