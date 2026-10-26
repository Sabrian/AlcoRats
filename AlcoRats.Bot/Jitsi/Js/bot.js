const puppeteer = require('puppeteer-core');

// НАСТРОЙКИ БОТА
const ROOM_URL = 'https://alkonost-our.site/alcorats'; // Ваша комната
const BOT_NAME = '[BOT] Ceiling_Cat';
const BOT_EMAIL = '###########'; // Ваш Email от Gravatar
const MODERATOR_USER = '##############';
const MODERATOR_PASS = '##############';

async function runBot() {
    console.log('Запуск бота Jitsi (LocalStorage-интеграция)...');

    const browser = await puppeteer.launch({
        executablePath: '/usr/bin/chromium-browser',
        headless: true,
        args: [
            '--use-fake-ui-for-media-stream',
            '--use-fake-device-for-media-stream',
            '--no-sandbox',
            '--disable-setuid-sandbox'
        ]
    });

    const page = await browser.newPage();

    // 1. Сначала переходим на домен сайта, чтобы инициализировать LocalStorage для этого адреса
    await page.goto('https://alkonost-our.site');

    // 2. Жестко записываем имя и email профиля в память браузера
    await page.evaluate((name, email) => {
        localStorage.setItem('features/base/profile', JSON.stringify({
            displayName: name,
            email: email
        }));
        // Дублируем в старый формат для обратной совместимости
        localStorage.setItem('displayname', name);
        localStorage.setItem('email', email);
    }, BOT_NAME, BOT_EMAIL);

    console.log(`Профиль бота успешно внедрен в кэш: ${BOT_NAME} (${BOT_EMAIL})`);

    // 3. Теперь переходим непосредственно в саму комнату
    await page.goto(`${ROOM_URL}#config.startWithAudioMuted=true&config.startWithVideoMuted=true`);
    await new Promise(resolve => setTimeout(resolve, 8000));

    try {
        // 4. Проверяем окно авторизации модератора (Secure Domain)
        const isLoginRequired = await page.$('input[name="username"]') !== null;
        if (isLoginRequired) {
            console.log('Обнаружен Secure Domain. Выполняю вход модератора...');
            await page.type('input[name="username"]', MODERATOR_USER);
            await page.type('input[name="password"]', MODERATOR_PASS);
            await page.click('button[type="submit"]');
            await new Promise(resolve => setTimeout(resolve, 6000));
        }
    } catch (e) { }

    try {
        // 5. Нажимаем кнопку входа (данные имени и email уже подтянулись из памяти автоматически)
        console.log('Нажимаю кнопку входа в конференцию...');
        const joinButton = await page.$('button[data-testid="prejoin.joinMeeting"]') || await page.$('.prejoin-page-button');
        if (joinButton) {
            await joinButton.click();
        } else {
            await page.keyboard.press('Enter');
        }
    } catch (uiError) {
        console.log('Ошибка при клике кнопки входа:', uiError.message);
    }

    await new Promise(resolve => setTimeout(resolve, 5000));
    console.log('Бот зафиксирован. Проверяйте аватарку Gravatar!');
}

runBot().catch(err => console.error('Критическая ошибка:', err));
