import { NextResponse } from "next/server";
import { BreakLine, CharacterSet, ThermalPrinter } from "node-thermal-printer";

const printer = new ThermalPrinter({
  interface: 'tcp://192.168.123.100',
  characterSet: CharacterSet.PC850_MULTILINGUAL,
  removeSpecialCharacters: false,
  lineCharacter: "=",
  breakLine: BreakLine.WORD,
  options:{
    timeout: 5000,
  },
});

export async function POST(req: Request) {
  try {
    const data = await req.json();

    console.log("Form submitted:", data);

    // Print the data
    console.log(await printer.isPrinterConnected());
    printer.clear();
    printer.alignCenter();
    printer.bold(true);
    printer.setTextDoubleHeight();
    printer.setTextDoubleWidth();
    printer.drawLine();
    printer.println(data.name);
    printer.drawLine();
    printer.println(data.message);
    printer.drawLine();
    printer.cut();
    await printer.execute();
    printer.clear();

    return NextResponse.json({ message: "Form submitted successfully", data });
  } catch (error) {
    return NextResponse.json({ message: "Error processing form", error }, { status: 500 });
  }
}
