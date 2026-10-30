<p align="center"><img src="src/PanDaF/Assets/PanDaF.png" width="112" alt="PanDaF"></p>
<h1 align="center">PanDaF</h1>
<p align="center">Öğretmenler için basit, Türkçe PDF araçları · Windows 10 / 11</p>

<p align="center"><a href="https://github.com/sinanince1/pdf/releases/latest/download/PanDaF.exe"><b>⬇ PanDaF'ı indir (Windows)</b></a></p>

## Neler yapabilir?

| Araç | Ne işe yarar? |
| --- | --- |
| **PDF birleştir** | Birden fazla PDF'yi istediğiniz sırayla tek dosyada toplar. |
| **PDF böl** | Her sayfayı ayrı PDF yapar, her N sayfada bir böler veya yalnızca istediğiniz sayfaları (ör. `1-3, 5`) alır. |
| **Sayfaları düzenle** | Sayfaları küçük resimleriyle gösterir; silme, sürükleyerek sıralama ve döndürme. Geri alma desteklidir. |
| **PDF'yi düzenle** | Sayfanın üzerine yazı kutusu ekleyin, bir alanı beyazla kapatıp üstüne yeni yazı yazın (düzeltme), vurgulayın, kalemle çizin, resim/imza ekleyin. **İSTEK** ve **IB** logoları tek tıkla, istenirse her sayfaya eklenir. Geri alma, taşıma ve boyutlandırma var. Asıl içerik değişmez; sonuç yeni dosya olur. |
| **PDF'ye çevir** | Word, PowerPoint, Excel dosyalarını ve fotoğrafları PDF yapar. Fotoğraflar A4'e yerleştirilebilir, tek PDF'de birleştirilebilir. |
| **PDF'den çevir** | PDF'yi Word belgesine, Excel tablosuna, her sayfası ayrı bir resme (JPG/PNG) veya düz metne dönüştürür. |

### Shift paneli (en hızlı yol)

PanDaF çalışırken masaüstünde veya bir klasörde bir ya da birkaç dosyayı seçip **sürüklemeye başlayın ve Shift'e basın**. İmlecin altında PanDaF paneli açılır; dosyaları bir dilimin üzerine bırakın:

| Sürüklediğiniz | Paneldeki seçenekler |
| --- | --- |
| Tek PDF | İçeriği düzenle · Sayfaları düzenle · Böl · **Word'e çevir** · **Excel'e çevir** · **Resme çevir** |
| Birden fazla PDF | Birleştir · **Word'e çevir** · **Excel'e çevir** · **Resme çevir** |
| Tek resim veya Word/PowerPoint/Excel | **PDF yap** · PanDaF'ta aç |
| Birden fazla resim | **Tek PDF yap** · **Ayrı PDF'ler** · PanDaF'ta aç |

**Kalın** seçenekler pencere açmadan ve soru sormadan çalışır: sonuç, asıl dosyanın bulunduğu klasöre aynı adla kaydedilir (ör. `Ödev.jpg` → `Ödev.pdf`). Ekranın sağ altında küçük bir bildirim çıkar; oradan dosyayı açabilir veya klasörde gösterebilirsiniz. Diğer seçenekler ilgili aracı dosyalar yüklenmiş olarak açar.

Vazgeçmek için dosyaları panelin ortasına ya da boş bir yere bırakın.

> Dosya bir klasörde değilse (tarayıcının indirilenler listesi, e-posta eki, ZIP içi, yalnızca çevrim içi OneDrive dosyası) Windows dosyanın yerini vermez; panel bunu söyler. Önce dosyayı masaüstüne kaydedip oradan sürükleyin.

### Arka planda çalışma ve otomatik başlatma

- PanDaF, **bilgisayar açılınca** kendiliğinden ve pencere açmadan başlar; saatin yanında panda simgesi görünür. Böylece Shift paneli her zaman hazırdır. Yönetici izni gerekmez.
- Pencereyi kapatmak PanDaF'ı kapatmaz; simgeye dönüşür. Tamamen kapatmak için simgeye sağ tıklayıp **PanDaF'tan çık**'ı seçin.
- Bunlar **Ayarlar** düğmesinden (veya simgeye sağ tıklayarak) açılıp kapatılabilir: *Shift paneli*, *Bilgisayar açılınca başlat*.

Dosyaları doğrudan pencereye sürükleyip bırakabilirsiniz: tek PDF bırakırsanız sayfa düzenleyici, birden fazla PDF bırakırsanız birleştirme, Word veya fotoğraf bırakırsanız PDF'ye çevirme açılır.

## Kurulum

1. Yukarıdaki bağlantıdan **PanDaF.exe** dosyasını indirin.
2. Dosyaya çift tıklayın. Kurulum gerekmez; dosyayı Masaüstüne veya istediğiniz klasöre koyabilirsiniz.
3. İlk açılışta Windows **"Windows bilgisayarınızı korudu"** diyebilir. **Ek bilgi**'ye, ardından **Yine de çalıştır**'a tıklayın. Bu uyarı, uygulamanın ücretli bir kod imzası taşımamasından kaynaklanır; bir kez onaylamanız yeterlidir.

> Okul bilgisayarında uygulama hiç açılmıyorsa, bilgisayarın yöneticisi bilinmeyen programları engellemiş olabilir. Bilişim sorumlusundan izin isteyin.

## Word, PowerPoint ve Excel dönüşümleri

- Bilgisayarda **Microsoft Office** kuruluysa dönüşümler Office'in kendisiyle yapılır; biçim en iyi şekilde korunur.
- Office yoksa ücretsiz **LibreOffice** bileşeni ilk kullanımda **bir kez** indirilir (yaklaşık 350 MB). Uygulama önce izninizi sorar. Bileşen bilgisayara kurulmaz, yalnızca `%LOCALAPPDATA%\PanDaF` klasörüne açılır.
- **PDF → Word** üç yolu sırayla dener: Microsoft Word'ün kendi dönüştürücüsü; LibreOffice'e PDF'yi doğrudan verme (sayfa düzeni korunur, Stirling-PDF'in de kullandığı yol); metni çıkarıp akıcı belge kurma. Taranmış (fotoğraf gibi) PDF'lerde Windows'un yazı tanıması kullanılır; belgeye sayfa görüntüsünün altında tanınan metin eklenir. Sonuç, PDF'nin nasıl hazırlandığına bağlıdır; hiçbir araç her PDF'yi kusursuz Word yapamaz.
- **PDF → Excel** ek program gerektirmez: sayfadaki sözcüklerin konumlarından satır ve sütunlar çıkarılır, sayılar sayı olarak yazılır. Çizgili tablolarda ve not listelerinde iyi çalışır; serbest metinde satırlar tek sütuna düşer.

## Gizlilik ve güvenlik

- Dosyalarınız **internete yüklenmez**; her şey bu bilgisayarda yapılır. İnternet yalnızca, Office olmayan bilgisayarda LibreOffice'in ilk indirilmesi için kullanılır.
- **Asıl dosyalarınız hiçbir zaman değiştirilmez.** Sonuçlar, asıl dosyanın bulunduğu klasöre yeni bir dosya olarak kaydedilir (aynı adda dosya varsa `Ad 2.pdf` gibi). O klasöre yazılamıyorsa `Belgeler\PanDaF` kullanılır.
- İşlem iptal edilirse veya hata olursa yarım dosya bırakılmaz.
- Doldurulabilir form alanı olan bir PDF'de, form alanlarının bozulabileceği işlemlerden önce uyarı gösterilir.

## Sorun mu var?

Hata ayrıntıları `%LOCALAPPDATA%\PanDaF\hata-gunlugu.txt`, Shift panelinin adımları `%LOCALAPPDATA%\PanDaF\panel-gunlugu.txt` dosyasına yazılır. Sorun bildirirken bu dosyayı eklemeniz çözümü kolaylaştırır.

---

## Geliştirenler için

- **Yapı:** `src/PanDaF.Core` (PDF motoru), `src/PanDaF` (WPF arayüzü), `tests/PanDaF.Tests` (motor testleri). .NET 10, WPF, x64.
- **Derleme:** `dotnet publish src/PanDaF/PanDaF.csproj -c Release -o build/app` → tek dosya `PanDaF.exe` (.NET kurulumu gerekmez).
- **Testler:** `dotnet run --project tests/PanDaF.Tests -c Release`. `PANDAF_LIBREOFFICE=1` ile LibreOffice gerçekten indirilir ve Office dönüşümleri de denenir.
- **Arayüz testi:** `PanDaF.exe --smoke <klasör>` tüm araçları gerçek pencerede, gerçek dosyalarla çalıştırır ve ekran görüntüleri alır.
- **Otomatik derleme:** Her `main` gönderiminde GitHub Actions Windows'ta testleri ve arayüz testini çalıştırır. Yeni sürüm için `src/PanDaF/PanDaF.csproj` içindeki `<Version>` numarasını artırıp `main`'e gönderin; testler geçerse sürüm (`v1.2.1` gibi) `.exe` ve `.zip` ile otomatik yayınlanır. İsterseniz GitHub'da **Releases → Draft a new release** ile elle de yayınlayabilirsiniz.

## Teşekkür ve lisans

PanDaF, [Orkun Işıtmak](https://github.com/orkunisitmak)'ın açık kaynaklı [Nashi](https://github.com/orkunisitmak/nashi-app) uygulamasının PDF motoru temel alınarak hazırlanmıştır. Nashi'nin sayfa birleştirme/bölme/sıralama motoru, PDFium ile sayfa çizimi, LibreOffice'in doğrulanmış kurulumu ve PDF'den metin çıkarma kodları uyarlanmış; arayüz öğretmenler için yeniden yazılmıştır.

[MIT lisansı](LICENSE) · [Üçüncü taraf bileşenler](THIRD-PARTY-NOTICES.txt) · Sinan İnce 2026
