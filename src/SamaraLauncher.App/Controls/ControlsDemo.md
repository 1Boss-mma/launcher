# Пиксельные контролы

Все контролы находятся в namespace `SamaraLauncher.App.Controls`, рисуются кодом через `DrawingContext` и не требуют картинок, шрифтов или NuGet-пакетов. Работают в `net8.0-windows` / WPF.

Подключение в XAML:

```xml
xmlns:controls="clr-namespace:SamaraLauncher.App.Controls"
```

Во фрагментах ниже это объявление подразумевается; полностью самостоятельный пример окна приведён в конце.

Палитра доступна как статические кисти (`PixelPalette.Green` и т. п.) и как цвета (`PixelPalette.GreenColor`). Кисти заморожены.

```xml
<Border Background="{x:Static controls:PixelPalette.Bg}" />
```

## PixelIcon

Пиксельная иконка 10×10, нарисованная прямоугольниками.

```xml
<controls:PixelIcon Kind="Settings"
                    PixelSize="2"
                    Brush="{x:Static controls:PixelPalette.Muted}" />
```

Свойства:

| Свойство | Тип | По умолчанию | Описание |
|---|---|---|---|
| `Kind` | `PixelIconKind` | `Home` | Символ иконки |
| `Brush` | `Brush` | `PixelPalette.Text` | Цвет пикселей |
| `PixelSize` | `double` | `2` | Размер одного пикселя иконки |

Доступные `Kind`: `Home`, `Mods`, `Worlds`, `Logs`, `Settings`, `Play`, `Refresh`, `Shield`, `Crash`, `Folder`, `User`, `Plus`, `Check`, `Warning`, `Download`, `Clock`, `Memory`, `Java`, `Server`, `Close`, `Minimize`, `Maximize`, `Back`, `Search`, `Trash`, `Power`, `News`, `Backup`, `Info`.

Иконку можно масштабировать через `Width`/`Height` — рисунок центрируется и при необходимости уменьшается.

## PixelFrame

Панель с тёмным фоном, рамкой 2 px и срезанными уголками.

```xml
<controls:PixelFrame Cut="8">
    <StackPanel Margin="10">
        <TextBlock Text="Настройки" Foreground="{x:Static controls:PixelPalette.Text}" />
        <controls:PixelSeparator Margin="0,8" />
    </StackPanel>
</controls:PixelFrame>
```

Свойства:

| Свойство | Тип | По умолчанию | Описание |
|---|---|---|---|
| `Cut` | `double` | `6` | Размер среза угла |
| `FrameBrush` | `Brush` | `PixelPalette.Line` | Внешняя обводка 2 px |
| `PanelBrush` | `Brush` | `PixelPalette.Panel` | Заливка панели |
| `InsetBrush` | `Brush` | `PixelPalette.Panel2` | Внутренняя линия 1 px |

Контент размещается с внутренним отступом 2 px. Для содержимого лучше задавать собственный `Margin`.

## PixelButton

Кнопка, полностью нарисованная в `OnRender`. Клик, клавиатура и `Command` работают как у обычного `Button`.

```xml
<controls:PixelButton Variant="Primary"
                      IconKind="Play"
                      Text="Играть"
                      Click="Play_Click" />

<controls:PixelButton Variant="Secondary"
                      IconKind="Refresh"
                      Text="Обновить" />

<controls:PixelButton Variant="Ghost"
                      IconKind="Settings"
                      Content="Настройки" />

<controls:PixelButton Variant="Danger"
                      IconKind="Trash"
                      Text="Удалить" />
```

Свойства:

| Свойство | Тип | По умолчанию | Описание |
|---|---|---|---|
| `Variant` | `PixelButtonVariant` | `Primary` | `Primary`, `Secondary`, `Ghost`, `Danger` |
| `IconKind` | `PixelIconKind?` | `null` | Иконка слева |
| `IconSize` | `double` | `18` | Сторона иконки |
| `Text` | `string?` | `null` | Текст; если пуст, берётся `Content`, если это строка |
| `Content` | `object` | — | Строка используется как текст, остальное не рисуется |

Высота по умолчанию 40, `Padding` — 14/8, шрифт Bahnschrift SemiBold 14. Кнопка сама рассчитывает ширину по тексту и иконке, поэтому текст не обрезается при `Width="Auto"`. Если `Content` — не строка, текст не отображается.

## PixelToggle

Переключатель 64×26 с подписью ВКЛ/ВЫКЛ.

```xml
<controls:PixelToggle IsChecked="True" />
<controls:PixelToggle IsChecked="{Binding IsNight, Mode=TwoWay}" />
```

Работает как обычный `ToggleButton`: клик меняет `IsChecked`, можно биндить `Command`.

| Свойство | Тип | По умолчанию | Описание |
|---|---|---|---|
| `IsChecked` | `bool?` | `false` | Включён ли переключатель |
| `IsThreeState` | `bool` | `false` | Наследуется от `ToggleButton` |
| `Command` | `ICommand` | `null` | Команда, вызываемая при клике |
| `CommandParameter` | `object` | `null` | Параметр команды |

Размер по умолчанию — 64×26; `Width` и `Height` можно переопределить, рисунок адаптируется.

## PixelProgressBar

Полоса прогресса / XP без анимации.

```xml
<controls:PixelProgressBar Value="64" ShowText="True" BarHeight="16" />

<controls:PixelProgressBar Value="42"
                           Level="12"
                           BarHeight="18"
                           ShowText="True" />
```

Свойства:

| Свойство | Тип | По умолчанию | Описание |
|---|---|---|---|
| `Value` | `double` | `0` | Значение 0..100, автоматически ограничивается |
| `ShowText` | `bool` | `true` | Показывать проценты или уровень |
| `BarHeight` | `double` | `16` | Высота полосы |
| `Level` | `int` | `0` | Если больше 0, выводится «УР. N» вместо процентов |
| `BarBrush` | `Brush` | `PixelPalette.Xp` | Заливка |
| `HighlightBrush` | `Brush` | `PixelPalette.XpHi` | Верхний блик |
| `TrackBrush` | `Brush` | `PixelPalette.BgDark` | Дорожка |
| `FrameBrush` | `Brush` | `PixelPalette.Line` | Рамка |

## PixelPanorama

Пиксельный пейзаж: небо, солнце/луна, облака, три слоя холмов, звёзды и частицы. Анимация включается только когда элемент видим и загружен.

```xml
<Grid>
    <controls:PixelPanorama IsNight="False"
                            ParticlesEnabled="True"
                            AnimationEnabled="True" />
    <StackPanel Margin="24" VerticalAlignment="Bottom">
        <TextBlock Text="Launcher"
                   FontFamily="Bahnschrift"
                   FontSize="32"
                   Foreground="{x:Static controls:PixelPalette.Text}" />
    </StackPanel>
</Grid>
```

Свойства:

| Свойство | Тип | По умолчанию | Описание |
|---|---|---|---|
| `IsNight` | `bool` | `false` | Ночная палитра: звёзды, луна, светлячки |
| `ParticlesEnabled` | `bool` | `true` | Частицы (пыльца днём, светлячки ночью) |
| `AnimationEnabled` | `bool` | `true` | Движение облаков и частиц |

Контрол растягивается по доступному месту. Если размер не задан контейнером, использует 800×450.

## PixelAvatar

Пиксельное лицо 8×8, цвета зависят от хеша ника.

```xml
<controls:PixelAvatar Username="{Binding Username}" Width="48" Height="48" />
<controls:PixelAvatar Username="Steve" />
```

Свойства:

| Свойство | Тип | По умолчанию | Описание |
|---|---|---|---|
| `Username` | `string?` | `""` | Ник для выбора палитры |

Размер по умолчанию 40×40; рисунок всегда квадратный и центрируется в слоте.

## PixelSeparator

Тонкая линия с пиксельными зазубринами.

```xml
<controls:PixelSeparator Margin="0,8" />

<controls:PixelSeparator Orientation="Vertical"
                          Height="32"
                          NotchSpacing="24" />
```

Свойства:

| Свойство | Тип | По умолчанию | Описание |
|---|---|---|---|
| `Orientation` | `Orientation` | `Horizontal` | Направление линии |
| `LineBrush` | `Brush` | `PixelPalette.Line` | Цвет линии |
| `NotchBrush` | `Brush` | полупрозрачный `Muted` | Цвет зазубрин |
| `NotchSpacing` | `double` | `32` | Шаг зазубрин |

## Пример собранного экрана

```xml
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:controls="clr-namespace:SamaraLauncher.App.Controls"
        Title="Launcher"
        Width="960" Height="600"
        Background="{x:Static controls:PixelPalette.Bg}">
    <Grid>
        <controls:PixelPanorama IsNight="True" />

        <Grid Margin="24">
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto" />
                <RowDefinition Height="*" />
                <RowDefinition Height="Auto" />
            </Grid.RowDefinitions>

            <StackPanel Orientation="Horizontal">
                <controls:PixelAvatar Username="Steve" Width="40" Height="40" />
                <TextBlock Text="Steve"
                           Margin="10,0,0,0"
                           VerticalAlignment="Center"
                           FontFamily="Bahnschrift"
                           FontSize="16"
                           Foreground="{x:Static controls:PixelPalette.Text}" />
            </StackPanel>

            <controls:PixelFrame Grid.Row="1" Margin="0,16" Cut="8">
                <StackPanel Margin="12">
                    <TextBlock Text="Новости"
                               FontFamily="Bahnschrift"
                               FontSize="20"
                               Foreground="{x:Static controls:PixelPalette.Text}" />
                    <controls:PixelSeparator Margin="0,8" />
                    <TextBlock Text="Сервер обновлён до 1.21.1"
                               Margin="0,8,0,0"
                               Foreground="{x:Static controls:PixelPalette.Muted}" />
                </StackPanel>
            </controls:PixelFrame>

            <Grid Grid.Row="2">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" />
                </Grid.ColumnDefinitions>

                <StackPanel VerticalAlignment="Center">
                    <controls:PixelProgressBar Value="72" ShowText="True" />
                </StackPanel>

                <StackPanel Grid.Column="1" Orientation="Horizontal">
                    <controls:PixelToggle IsChecked="True" Margin="0,0,12,0" />
                    <controls:PixelButton Variant="Primary"
                                          IconKind="Play"
                                          Text="Играть"
                                          Width="160"
                                          Margin="0,0,12,0" />
                    <controls:PixelButton Variant="Secondary"
                                          IconKind="Settings"
                                          Text="Настройки" />
                </StackPanel>
            </Grid>
        </Grid>
    </Grid>
</Window>
```
