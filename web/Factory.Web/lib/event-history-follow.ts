export function isNearBottom(scrollHeight: number, scrollTop: number, clientHeight: number, threshold = 32) {
  return scrollHeight - scrollTop - clientHeight <= threshold;
}
