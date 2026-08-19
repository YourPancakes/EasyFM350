using System.Collections.Generic;
using System.Threading;

namespace EasyFM350.Wpf.Backend.Config;

public static class Lang
{
    public enum Id
    {
        Ru,
        En
    }

    private static int _current = (int)Id.En;

    private static readonly Dictionary<string, string[]> Table = new()
    {
        { "st_error", new[] { "Ошибка", "Error" } },
        { "band", new[] { "Бэнд", "Band" } },
        { "ca_none", new[] { "-", "-" } },
        { "tab_dash", new[] { "Обзор", "Dashboard" } },
        { "tab_bands", new[] { "Бэнды и режим", "Bands & mode" } },
        { "tab_apn", new[] { "APN", "APN" } },
        { "tab_net", new[] { "Сеть", "Network" } },
        { "tab_device", new[] { "Модем", "Modem" } },
        { "tab_log", new[] { "Лог", "Log" } },
        { "mode_auto", new[] { "Авто (5G/4G/3G)", "Auto (5G/4G/3G)" } },
        { "mode_5g4g", new[] { "5G+4G", "5G+4G" } },
        { "mode_4g", new[] { "Только 4G", "4G only" } },
        { "mode_3g", new[] { "Только 3G", "3G only" } },
        { "mode_5gsa", new[] { "Только 5G", "5G only" } },
        { "5gopt_auto", new[] { "5G: Авто (NSA+SA)", "5G: Auto (NSA+SA)" } },
        { "5gopt_sa", new[] { "5G: только SA", "5G: SA only" } },
        { "5gopt_nsa", new[] { "5G: только NSA", "5G: NSA only" } },

        { "auth_none", new[] { "Нет", "None" } },

        { "net_state", new[] { "Состояние", "State" } },
        { "net_on", new[] { "Включено", "On" } },
        { "net_off", new[] { "Выключено", "Off" } },
        { "ui_signal", new[] { "Сигнал", "Signal" } },
        { "ui_chart", new[] { "RSRP", "RSRP" } },
        { "ui_mode", new[] { "Режим:", "Mode:" } },
        { "ui_dev_title", new[] { "Параметры модема", "Modem parameters" } },
        { "ui_refresh_now", new[] { "Обновить сейчас", "Refresh now" } },
        { "ui_apn_section", new[] { "Точка доступа", "Access point" } },
        { "ui_pdp", new[] { "Протокол точки доступа", "Access point protocol" } },
        { "ui_auth", new[] { "Аутентификация", "Authentication" } },
        { "ui_user", new[] { "Пользователь", "Username" } },
        { "ui_pass", new[] { "Пароль", "Password" } },
        { "ui_apnproxy", new[] { "Прокси", "Proxy" } },
        { "ui_apn_write", new[] { "Записать в модем", "Write to modem" } },
        { "ui_apn_saved", new[] { "Записано", "Saved" } },
        { "ui_net_proxy", new[] { "Прокси", "Proxy" } },
        { "ui_net_tun", new[] { "TUN", "TUN" } },
        { "ui_tun_warn", new[] { "Требуются права администратора", "Administrator rights required" } },
        { "dev_model", new[] { "Модель/ПО", "Model/FW" } },
        { "dev_rfw", new[] { "RF HW", "RF hardware" } },
        { "dev_ecal", new[] { "Калибровка (ECAL)", "Calibration" } },
        { "dev_qflag", new[] { "Флаг калибровки", "Calibration flag" } },
        { "dev_oper", new[] { "Оператор/регистрация", "Operator" } },
        { "dev_bands", new[] { "Режим/бэнды (GTACT)", "Mode & bands" } },
        { "dev_5gopt", new[] { "5G опции", "5G options" } },
        { "dev_ca", new[] { "Агрегация (CA)", "Carrier aggregation" } },
        { "dev_bandcfg", new[] { "Вкл/выкл бэндов", "Band switches" } },
        { "dev_dualsim", new[] { "Dual SIM", "Dual SIM" } },
        { "dev_temp", new[] { "Температура", "Temperature" } },
        { "dev_txp", new[] { "TX power", "TX power" } },
        { "dev_pdnip", new[] { "PDN IP", "IP address" } },
        { "empty_hint", new[] { "Модем не подключен.", "Modem not connected." } },
        { "tray_open", new[] { "Открыть", "Open" } },
        { "tray_exit", new[] { "Выход", "Exit" } },
        { "esim_title", new[] { "Профили eSIM", "eSIM profiles" } },
        { "esim_empty", new[] { "Профилей нет", "No profiles" } },
        { "esim_download", new[] { "Записать", "Write" } },
        { "esim_delete", new[] { "Удалить", "Delete" } },
        { "esim_confirm", new[] { "Подтвердить?", "Confirm?" } },
        { "esim_free", new[] { "свободно", "free" } },
        { "esim_euicc_wait", new[] { "Перезагрузка eUICC, ожидание…", "eUICC resetting, waiting…" } },
        { "esim_op_chip", new[] { "Чтение данных чипа…", "Reading chip info…" } },
        { "esim_op_profiles", new[] { "Чтение профилей…", "Reading profiles…" } },
        { "esim_op_notifications", new[] { "Обработка уведомлений…", "Processing notifications…" } },
        { "esim_op_enable", new[] { "Переключение профиля…", "Switching profile…" } },
        { "esim_op_disable", new[] { "Отключение профиля…", "Disabling profile…" } },
        { "esim_op_delete", new[] { "Удаление профиля…", "Deleting profile…" } },
        { "esim_op_download", new[] { "Запись профиля…", "Downloading profile…" } },
        { "esim_resync", new[] { "Перезапуск SIM-слота…", "Restarting SIM slot…" } },
        { "esim_modem_reset", new[] { "Модем завис, полная перезагрузка…", "Modem wedged, full restart…" } }
    };

    public static Id Current
    {
        get => (Id)Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, (int)value);
    }

    public static string T(string key)
    {
        return T(key, Current);
    }

    public static string T(string key, Id lang)
    {
        if (Table.TryGetValue(key, out var value)) return value[(int)lang];
        return key;
    }
}