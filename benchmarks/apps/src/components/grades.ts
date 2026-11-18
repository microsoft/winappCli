export function getGrade(score: number): { letter: string; color: string } {
  if (score >= 95) return { letter: "A+", color: "green" };
  if (score >= 90) return { letter: "A", color: "green" };
  if (score >= 85) return { letter: "B+", color: "greenBright" };
  if (score >= 80) return { letter: "B", color: "greenBright" };
  if (score >= 75) return { letter: "C+", color: "yellow" };
  if (score >= 70) return { letter: "C", color: "yellow" };
  if (score >= 60) return { letter: "D", color: "yellowBright" };
  return { letter: "F", color: "red" };
}
