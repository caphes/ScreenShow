package app.screenshow;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.SharedPreferences;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Color;
import android.graphics.Insets;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.RippleDrawable;
import android.content.res.ColorStateList;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.InputFilter;
import android.text.InputType;
import android.view.Gravity;
import android.view.View;
import android.view.WindowInsets;
import android.view.WindowManager;
import android.view.inputmethod.EditorInfo;
import android.view.inputmethod.InputMethodManager;
import android.widget.Button;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import java.io.IOException;
import java.util.ArrayList;
import java.util.Comparator;

public final class MainActivity extends Activity {
    private static final int BACKGROUND = Color.rgb(239, 246, 241);
    private static final int PANEL = Color.WHITE;
    private static final int FIELD = Color.rgb(230, 241, 234);
    private static final int BORDER = Color.rgb(218, 232, 222);
    private static final int INK = Color.rgb(20, 44, 34);
    private static final int MUTED = Color.rgb(73, 99, 86);
    private static final int ACCENT = Color.rgb(23, 104, 72);
    private static final int DANGER = Color.rgb(159, 42, 57);
    private static final int DANGER_FILL = Color.rgb(252, 234, 236);
    static final int VIEW_BACKGROUND = Color.rgb(12, 26, 20);
    static final int VIEW_HINT = Color.rgb(174, 202, 187);

    private final Handler ui = new Handler(Looper.getMainLooper());
    private FrameLayout root;
    private EditText code;
    private TextView status;
    private Button join, paste, cancel;
    private ViewConnection active;
    private RemoteScreen screen;
    private AlertDialog dialog;
    private final ArrayList<Button> devices = new ArrayList<>();

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        root = new FrameLayout(this);
        root.setBackground(new GradientDrawable(GradientDrawable.Orientation.TOP_BOTTOM,
            new int[] { Color.rgb(226, 241, 232), BACKGROUND, Color.rgb(247, 250, 248) }));
        root.setOnApplyWindowInsetsListener((view, insets) -> {
            if (Build.VERSION.SDK_INT >= 30) {
                Insets edges = insets.getInsets(WindowInsets.Type.systemBars() | WindowInsets.Type.displayCutout());
                view.setPadding(edges.left, edges.top, edges.right, edges.bottom);
            } else {
                view.setPadding(insets.getSystemWindowInsetLeft(), insets.getSystemWindowInsetTop(),
                    insets.getSystemWindowInsetRight(), insets.getSystemWindowInsetBottom());
            }
            return insets;
        });
        setContentView(root);
        home("", false);
    }

    private void home(String message, boolean error) {
        getWindow().clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        root.removeAllViews();
        devices.clear();
        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        FrameLayout center = new FrameLayout(this);
        scroll.addView(center);
        root.addView(scroll, new FrameLayout.LayoutParams(-1, -1));

        LinearLayout content = column();
        FrameLayout.LayoutParams contentLayout = new FrameLayout.LayoutParams(-1, -2,
            Gravity.CENTER_HORIZONTAL | Gravity.TOP);
        center.addView(content, contentLayout);
        center.addOnLayoutChangeListener((view, left, top, right, bottom, oldLeft, oldTop, oldRight, oldBottom) -> {
            int width = Math.min(right - left, dp(520));
            if (contentLayout.width != width && width > 0) {
                contentLayout.width = width;
                content.setLayoutParams(contentLayout);
            }
        });
        content.setPadding(dp(20), dp(30), dp(20), dp(26));
        content.setFocusableInTouchMode(true);
        content.requestFocus();

        LinearLayout brand = new LinearLayout(this);
        brand.setGravity(Gravity.CENTER_VERTICAL);
        ImageView icon = new ImageView(this);
        icon.setImageResource(R.drawable.ic_screenshow);
        brand.addView(icon, new LinearLayout.LayoutParams(dp(56), dp(56)));
        TextView title = label("ScreenShow", 34, INK, true);
        title.setLetterSpacing(-0.03f);
        LinearLayout.LayoutParams titleLayout = new LinearLayout.LayoutParams(-2, -2);
        titleLayout.leftMargin = dp(12);
        brand.addView(title, titleLayout);
        add(content, brand, 0);

        LinearLayout card = column();
        card.setBackground(cardBackground());
        card.setElevation(dp(2));
        card.setPadding(dp(21), dp(22), dp(21), dp(21));
        add(content, card, 26);
        add(card, label("Подключиться к ПК", 23, INK, true), 0);
        add(card, label("Вставьте код из ScreenShow на ПК, чтобы связать устройства.", 15, MUTED, false), 8);

        add(card, label("Код приглашения", 14, MUTED, true), 20);
        code = new EditText(this);
        code.setTextColor(INK);
        code.setHintTextColor(MUTED);
        code.setHint("SS3-…");
        code.setTextSize(16);
        code.setSingleLine(true);
        code.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        code.setTypeface(Typeface.MONOSPACE);
        code.setImeOptions(EditorInfo.IME_ACTION_GO | EditorInfo.IME_FLAG_NO_PERSONALIZED_LEARNING);
        code.setFilters(new InputFilter[] { new InputFilter.LengthFilter(256) });
        code.setSaveEnabled(false);
        code.setContentDescription("Код приглашения");
        code.setPadding(dp(12), dp(9), dp(12), dp(9));
        code.setMinHeight(dp(48));
        code.setBackground(shape(FIELD, 13));
        add(card, code, 7);
        code.setOnEditorActionListener((view, action, event) -> {
            if (action != EditorInfo.IME_ACTION_GO) return false;
            connect();
            return true;
        });

        LinearLayout buttons = new LinearLayout(this);
        paste = button("Вставить", false, INK);
        paste.setOnClickListener(view -> paste());
        join = button("Подключиться", true, INK);
        join.setOnClickListener(view -> connect());
        rowButton(buttons, paste, 0, 1f);
        rowButton(buttons, join, 8, 1.55f);
        add(card, buttons, 12);
        cancel = button("Отменить подключение", false, DANGER);
        cancel.setOnClickListener(view -> disconnect("Подключение отменено."));
        cancel.setVisibility(View.GONE);
        add(card, cancel, 8);

        status = label("", 14, MUTED, false);
        status.setBackground(shape(FIELD, 13));
        status.setPadding(dp(13), dp(10), dp(13), dp(10));
        status.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE);
        add(content, status, 14);
        setStatus(message, error);
        addDevices(content);
    }

    private SharedPreferences pairing() {
        return getSharedPreferences("screen_show_pairing", MODE_PRIVATE);
    }

    private void addDevices(LinearLayout content) {
        ArrayList<RemoteSession.Invitation> saved = new ArrayList<>();
        for (Object value : pairing().getAll().values()) {
            if (value instanceof String) {
                try { saved.add(RemoteSession.Invitation.parse((String) value)); }
                catch (IllegalArgumentException ignored) { }
            }
        }
        if (saved.isEmpty()) return;
        saved.sort(Comparator.comparing(invite -> invite.address.toString()));
        add(content, label("Связанные компьютеры", 18, INK, true), 17);
        for (RemoteSession.Invitation invitation : saved) {
            LinearLayout row = new LinearLayout(this);
            row.setBackground(cardBackground());
            row.setPadding(dp(8), dp(8), dp(8), dp(8));
            Button open = button(invitation.address.getAddress().getHostAddress() + ":" + invitation.address.getPort(),
                false, INK);
            open.setContentDescription("Подключиться к " + open.getText());
            open.setOnClickListener(view -> connect(invitation));
            Button remove = button("Забыть", false, DANGER);
            remove.setOnClickListener(view -> {
                pairing().edit().remove(invitation.deviceKey()).apply();
                home("", false);
            });
            rowButton(row, open, 0, 2.2f);
            rowButton(row, remove, 8, 1f);
            devices.add(open);
            devices.add(remove);
            add(content, row, 7);
        }
    }

    private void paste() {
        ClipboardManager clipboard = getSystemService(ClipboardManager.class);
        ClipData clip = clipboard == null ? null : clipboard.getPrimaryClip();
        CharSequence text = clip == null || clip.getItemCount() == 0 ? null : clip.getItemAt(0).getText();
        if (text == null || text.length() == 0) {
            setStatus("В буфере обмена нет кода приглашения.", true);
            return;
        }
        code.setText(text.toString().trim());
        code.setSelection(code.length());
        setStatus("", false);
    }

    private void connect() {
        if (active != null) return;
        RemoteSession.Invitation invitation;
        try {
            invitation = RemoteSession.Invitation.parse(code.getText().toString());
        } catch (IllegalArgumentException error) {
            setStatus(error.getMessage(), true);
            return;
        }
        connect(invitation);
    }

    private void connect(RemoteSession.Invitation invitation) {
        if (active != null) return;
        getSystemService(InputMethodManager.class).hideSoftInputFromWindow(code.getWindowToken(), 0);
        active = new ViewConnection(invitation);
        setPending(true);
        setStatus("Подключаемся к компьютеру…", false);
        active.remote.start();
    }

    private void showViewer(ViewConnection connection, boolean canControl, boolean canType) {
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        code.setText("");
        root.removeAllViews();
        LinearLayout layout = column();
        root.addView(layout, new FrameLayout.LayoutParams(-1, -1));
        screen = new RemoteScreen(this, connection.remote);
        layout.addView(screen, new LinearLayout.LayoutParams(-1, 0, 1));

        LinearLayout toolbar = column();
        toolbar.setBackgroundColor(PANEL);
        toolbar.setPadding(dp(10), dp(6), dp(10), dp(8));
        TextView mode = label(canControl ? "Просмотр · ввод выключен" : "Только просмотр", 13, ACCENT, true);
        mode.setPadding(dp(5), 0, dp(5), 0);
        LinearLayout viewOptions = new LinearLayout(this);
        viewOptions.setGravity(Gravity.CENTER_VERTICAL);
        viewOptions.addView(mode, new LinearLayout.LayoutParams(0, -2, 1));
        Button reset = button("По размеру", false, INK);
        reset.setContentDescription("Уместить экран целиком");
        reset.setOnClickListener(view -> screen.resetZoom());
        viewOptions.addView(reset, new LinearLayout.LayoutParams(dp(110), -2));
        toolbar.addView(viewOptions);
        LinearLayout actions = new LinearLayout(this);
        Button close = button("Отключиться", false, DANGER);
        close.setOnClickListener(view -> disconnect("Сеанс завершён."));
        if (canControl) {
            Button input = button("Включить ввод", true, INK);
            Button keys = button("Клавиши", false, INK);
            keys.setEnabled(false);
            input.setOnClickListener(new View.OnClickListener() {
                private boolean controlling = false;

                @Override public void onClick(View view) {
                    controlling = !controlling;
                    screen.setControlling(controlling);
                    input.setText(controlling ? "Выключить ввод" : "Включить ввод");
                    mode.setText(controlling ? "Управление активно" : "Просмотр · ввод выключен");
                    keys.setEnabled(controlling);
                }
            });
            keys.setOnClickListener(view -> showKeys(connection, canType));
            rowButton(actions, input, 0, 1.1f);
            rowButton(actions, keys, 6, 0.85f);
        }
        rowButton(actions, close, canControl ? 6 : 0, 1f);
        add(toolbar, actions, 4);
        layout.addView(toolbar, new LinearLayout.LayoutParams(-1, -2));
    }

    private void showKeys(ViewConnection connection, boolean canType) {
        String[] labels = canType ? new String[] { "Ввести текст", "Enter", "Пробел", "Tab", "Backspace",
            "Esc", "←", "↑", "→", "↓" } : new String[] { "Enter", "Пробел", "Tab", "Backspace", "Esc",
            "←", "↑", "→", "↓" };
        int[] keys = { 13, 32, 9, 8, 27, 37, 38, 39, 40 };
        showDialog(new AlertDialog.Builder(this).setTitle("Клавиатура ПК").setItems(labels, (window, index) -> {
            if (active != connection) return;
            if (canType && index == 0) {
                showTextEntry(connection);
            } else {
                int key = keys[canType ? index - 1 : index];
                sendKey(connection, key);
            }
        }).create());
    }

    private void showTextEntry(ViewConnection connection) {
        EditText field = new EditText(this);
        field.setHint("Текст для текущего окна ПК");
        field.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_MULTI_LINE);
        field.setImeOptions(EditorInfo.IME_FLAG_NO_PERSONALIZED_LEARNING);
        field.setMinLines(2);
        field.setMaxLines(4);
        field.setFilters(new InputFilter[] { new InputFilter.LengthFilter(240) });
        LinearLayout area = column();
        area.setPadding(dp(18), 0, dp(18), 0);
        area.addView(field, new LinearLayout.LayoutParams(-1, -2));
        AlertDialog entry = new AlertDialog.Builder(this).setTitle("Ввести текст").setView(area)
            .setNegativeButton("Отмена", null).setPositiveButton("Отправить", (window, button) -> {
                if (active != connection) return;
                String text = field.getText().toString();
                for (int index = 0; index < text.length(); ) {
                    int character = text.codePointAt(index);
                    index += Character.charCount(character);
                    if (character == '\n') sendKey(connection, 13);
                    else if (character == '\t') sendKey(connection, 9);
                    else if (!Character.isISOControl(character) && (character < 0xD800 || character > 0xDFFF))
                        connection.remote.queue(new RemoteSession.Command(RemoteSession.TEXT, 0, 0, character));
                }
            }).create();
        field.requestFocus();
        entry.getWindow().setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_STATE_ALWAYS_VISIBLE);
        showDialog(entry);
    }

    private static void sendKey(ViewConnection connection, int key) {
        int extended = key >= 37 && key <= 40 ? 512 : 0;
        connection.remote.queue(new RemoteSession.Command(RemoteSession.KEY, 0, 0, key | extended | 256));
        connection.remote.queue(new RemoteSession.Command(RemoteSession.KEY, 0, 0, key | extended));
    }

    private void showDialog(AlertDialog value) {
        dialog = value;
        value.setOnDismissListener(window -> { if (dialog == value) dialog = null; });
        value.show();
    }

    private void setPending(boolean pending) {
        code.setEnabled(!pending);
        paste.setEnabled(!pending);
        join.setEnabled(!pending);
        for (Button device : devices) device.setEnabled(!pending);
        cancel.setVisibility(pending ? View.VISIBLE : View.GONE);
    }

    private void setStatus(String message, boolean error) {
        status.setText(message);
        status.setTextColor(error ? DANGER : MUTED);
        status.setBackground(shape(error ? DANGER_FILL : FIELD, 13));
        status.setVisibility(message.isEmpty() ? View.GONE : View.VISIBLE);
    }

    private void disconnect(String message) {
        if (active != null) end(active, message, false);
    }

    private void end(ViewConnection connection, String message, boolean error) {
        if (active != connection) return;
        active = null;
        if (dialog != null) dialog.dismiss();
        if (screen != null) screen.setControlling(false);
        connection.close();
        if (screen != null) {
            screen = null;
            home(message, error);
        } else {
            setPending(false);
            setStatus(message, error);
        }
    }

    @Override public void onBackPressed() {
        if (active == null) super.onBackPressed();
        else disconnect("Сеанс завершён.");
    }

    @Override protected void onPause() {
        if (screen != null) screen.release();
        super.onPause();
    }

    @Override protected void onStop() {
        disconnect("Сеанс завершён.");
        super.onStop();
    }

    private int dp(float value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }

    private LinearLayout column() {
        LinearLayout layout = new LinearLayout(this);
        layout.setOrientation(LinearLayout.VERTICAL);
        return layout;
    }

    private void add(LinearLayout parent, View view, int top) {
        LinearLayout.LayoutParams parameters = new LinearLayout.LayoutParams(-1, -2);
        parameters.topMargin = dp(top);
        parent.addView(view, parameters);
    }

    private void rowButton(LinearLayout row, Button button, int left, float weight) {
        LinearLayout.LayoutParams parameters = new LinearLayout.LayoutParams(0, -2, weight);
        parameters.leftMargin = dp(left);
        row.addView(button, parameters);
    }

    private TextView label(String text, int size, int color, boolean bold) {
        TextView label = new TextView(this);
        label.setText(text);
        label.setTextSize(size);
        label.setTextColor(color);
        label.setTypeface(Typeface.create(bold ? "sans-serif-medium" : "sans-serif", Typeface.NORMAL));
        return label;
    }

    private Button button(String text, boolean primary, int color) {
        Button button = new Button(this);
        button.setText(text);
        button.setAllCaps(false);
        button.setTextSize(14);
        button.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        int[][] states = { new int[] { -android.R.attr.state_enabled }, new int[0] };
        button.setTextColor(new ColorStateList(states, new int[] { MUTED, primary ? Color.WHITE : color }));
        int resting = primary ? ACCENT : color == DANGER ? DANGER_FILL : FIELD;
        GradientDrawable fill = shape(resting, 13);
        fill.setColor(new ColorStateList(states, new int[] { FIELD, resting }));
        button.setBackground(new RippleDrawable(ColorStateList.valueOf(0x22509A71), fill, null));
        button.setMinWidth(0);
        button.setMinHeight(dp(48));
        button.setPadding(dp(5), dp(8), dp(5), dp(8));
        button.setStateListAnimator(null);
        return button;
    }

    private GradientDrawable shape(int fill, int radius) {
        GradientDrawable result = new GradientDrawable();
        result.setColor(fill);
        result.setCornerRadius(dp(radius));
        return result;
    }

    private GradientDrawable cardBackground() {
        GradientDrawable result = shape(PANEL, 23);
        result.setStroke(dp(1), BORDER);
        return result;
    }

    private final class ViewConnection implements RemoteSession.Listener {
        private final RemoteSession remote;
        private Bitmap pendingFrame;
        private boolean posted, closed;

        private ViewConnection(RemoteSession.Invitation invitation) {
            remote = new RemoteSession(invitation, this);
        }

        @Override public void waiting() {
            ui.post(() -> {
                if (active == this) setStatus("Открываем защищённый сеанс…", false);
            });
        }

        @Override public void connected(boolean control, boolean text, RemoteSession.Invitation linked) {
            ui.post(() -> {
                if (active != this) return;
                if (linked.canSave()) pairing().edit().putString(linked.deviceKey(), linked.code).apply();
                showViewer(this, control, text);
            });
        }

        @Override public void frame(byte[] jpeg) throws IOException {
            BitmapFactory.Options options = new BitmapFactory.Options();
            options.inJustDecodeBounds = true;
            BitmapFactory.decodeByteArray(jpeg, 0, jpeg.length, options);
            if (!"image/jpeg".equals(options.outMimeType) || options.outWidth < 1 || options.outWidth > 1600 ||
                options.outHeight < 1 || options.outHeight > 1000)
                throw new IOException("Компьютер прислал кадр в неподдерживаемом формате.");
            options.inJustDecodeBounds = false;
            Bitmap bitmap = BitmapFactory.decodeByteArray(jpeg, 0, jpeg.length, options);
            if (bitmap == null) throw new IOException("Не удалось прочитать изображение с компьютера.");

            synchronized (this) {
                if (closed) {
                    bitmap.recycle();
                    return;
                }
                if (pendingFrame != null) pendingFrame.recycle();
                pendingFrame = bitmap;
                if (!posted) {
                    posted = true;
                    ui.post(this::displayFrame);
                }
            }
        }

        private void displayFrame() {
            Bitmap frame;
            synchronized (this) {
                frame = pendingFrame;
                pendingFrame = null;
                posted = false;
            }
            if (frame == null) return;
            if (active == this && screen != null) screen.setFrame(frame);
            else frame.recycle();
        }

        @Override public void ended(String message, boolean error) {
            ui.post(() -> end(this, message, error));
        }

        private void close() {
            synchronized (this) {
                closed = true;
                if (pendingFrame != null) pendingFrame.recycle();
                pendingFrame = null;
            }
            remote.stop();
        }
    }
}
