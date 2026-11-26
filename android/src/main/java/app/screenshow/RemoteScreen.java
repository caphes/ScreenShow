package app.screenshow;

import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.graphics.RectF;
import android.view.MotionEvent;
import android.view.View;
import android.view.ViewConfiguration;

final class RemoteScreen extends View {
    private final Paint imagePaint = new Paint(Paint.FILTER_BITMAP_FLAG);
    private final Paint labelPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final RectF imageBounds = new RectF();
    private final RemoteSession session;
    private final int touchSlop;
    private final float wheelStep;
    private final Runnable hold = this::rightClick;
    private Bitmap frame;
    private boolean controlling, pointing, dragging, consumed, scrolling;
    private float startX, startY, previousY, remainingScroll;

    RemoteScreen(Context context, RemoteSession session) {
        super(context);
        this.session = session;
        touchSlop = ViewConfiguration.get(context).getScaledTouchSlop();
        wheelStep = 35 * getResources().getDisplayMetrics().density;
        labelPaint.setColor(MainActivity.MUTED);
        labelPaint.setTextAlign(Paint.Align.CENTER);
        labelPaint.setTextSize(15 * getResources().getDisplayMetrics().scaledDensity);
        setBackgroundColor(MainActivity.VIEW_BACKGROUND);
        setContentDescription("Удалённый экран");
    }

    void setFrame(Bitmap bitmap) {
        frame = bitmap;
        updateImageBounds();
        invalidate();
    }

    void setControlling(boolean enabled) {
        if (!enabled) release();
        controlling = enabled;
    }

    void release() {
        removeCallbacks(hold);
        pointing = dragging = scrolling = consumed = false;
        if (controlling) session.queue(new RemoteSession.Command(RemoteSession.RELEASE, 0, 0, 0));
    }

    @Override protected void onSizeChanged(int width, int height, int oldWidth, int oldHeight) {
        updateImageBounds();
    }

    private void updateImageBounds() {
        if (frame == null || getWidth() == 0 || getHeight() == 0) {
            imageBounds.setEmpty();
            return;
        }
        float scale = Math.min((float) getWidth() / frame.getWidth(), (float) getHeight() / frame.getHeight());
        float width = frame.getWidth() * scale;
        float height = frame.getHeight() * scale;
        imageBounds.set((getWidth() - width) / 2, (getHeight() - height) / 2,
            (getWidth() + width) / 2, (getHeight() + height) / 2);
    }

    @Override protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        if (frame != null) {
            canvas.drawBitmap(frame, null, imageBounds, imagePaint);
        } else {
            canvas.drawText("Ожидаем изображение…", getWidth() / 2f, getHeight() / 2f, labelPaint);
        }
    }

    @Override public boolean onTouchEvent(MotionEvent event) {
        if (!controlling || imageBounds.isEmpty()) return false;
        float x = event.getX();
        float y = event.getY();
        switch (event.getActionMasked()) {
            case MotionEvent.ACTION_DOWN:
                if (!imageBounds.contains(x, y)) return false;
                pointing = true;
                startX = x;
                startY = y;
                send(RemoteSession.MOVE, x, y, 0);
                postDelayed(hold, ViewConfiguration.getLongPressTimeout());
                return true;
            case MotionEvent.ACTION_POINTER_DOWN:
                if (!pointing) return false;
                removeCallbacks(hold);
                if (dragging) send(RemoteSession.BUTTON, x, y, 1);
                dragging = false;
                consumed = scrolling = true;
                remainingScroll = 0;
                previousY = (y + event.getY(1)) / 2;
                return true;
            case MotionEvent.ACTION_MOVE:
                if (!pointing) return false;
                if (scrolling && event.getPointerCount() > 1) {
                    float middleY = (y + event.getY(1)) / 2;
                    remainingScroll += middleY - previousY;
                    previousY = middleY;
                    int steps = Math.max(-10, Math.min(10, (int) (remainingScroll / wheelStep)));
                    if (steps != 0) {
                        send(RemoteSession.WHEEL, (x + event.getX(1)) / 2, middleY, steps * 120);
                        remainingScroll -= steps * wheelStep;
                    }
                } else if (!consumed) {
                    if (!dragging && Math.hypot(x - startX, y - startY) > touchSlop) {
                        removeCallbacks(hold);
                        send(RemoteSession.BUTTON, startX, startY, 257);
                        dragging = true;
                    }
                    if (dragging) send(RemoteSession.MOVE, x, y, 0);
                }
                return true;
            case MotionEvent.ACTION_POINTER_UP:
                scrolling = false;
                return pointing;
            case MotionEvent.ACTION_UP:
                if (!pointing) return false;
                removeCallbacks(hold);
                if (dragging) {
                    send(RemoteSession.BUTTON, x, y, 1);
                } else if (!consumed) {
                    send(RemoteSession.BUTTON, x, y, 257);
                    send(RemoteSession.BUTTON, x, y, 1);
                    performClick();
                }
                pointing = dragging = scrolling = consumed = false;
                return true;
            case MotionEvent.ACTION_CANCEL:
                release();
                return true;
            default:
                return pointing;
        }
    }

    @Override public boolean performClick() {
        super.performClick();
        return true;
    }

    private void rightClick() {
        if (!pointing || dragging || consumed) return;
        consumed = true;
        send(RemoteSession.BUTTON, startX, startY, 258);
        send(RemoteSession.BUTTON, startX, startY, 2);
    }

    private void send(int kind, float x, float y, int value) {
        int horizontal = normalize(x, imageBounds.left, imageBounds.width());
        int vertical = normalize(y, imageBounds.top, imageBounds.height());
        session.queue(new RemoteSession.Command(kind, horizontal, vertical, value));
    }

    private static int normalize(float coordinate, float origin, float extent) {
        return Math.max(0, Math.min(65535, Math.round((coordinate - origin) * 65535 / Math.max(1, extent - 1))));
    }
}
