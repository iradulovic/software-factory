if (process.geteuid === undefined) {
  process.geteuid = () => 0;
}
