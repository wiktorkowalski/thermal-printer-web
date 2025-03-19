import { NextResponse } from "next/server";
import { BreakLine, CharacterSet, ThermalPrinter } from "node-thermal-printer";
import sharp from "sharp";
import { NextRequest } from 'next/server';

const printer = new ThermalPrinter({
  interface: 'tcp://192.168.123.100',
  characterSet: CharacterSet.PC852_LATIN2,
  removeSpecialCharacters: false,
  lineCharacter: "=",
  breakLine: BreakLine.WORD,
  options:{
    timeout: 5000,
  },
});

// const formSchema = zod.object({
//   name: zod.string().min(1).max(50),
//   message: zod.string().min(1).max(5000),
//   image: zod.any(),
// });

export async function POST(req: NextRequest) {
  try {
    const formData = await req.formData();
    const name = formData.get('name') as string;
    const message = formData.get('message') as string;
    const image: File | null = formData.get('image') as unknown as File | null;

    console.log("Form submitted:", { name, message, image: image ? image.name : null });
    console.log(formData);
    console.log(image);

    console.log(await printer.isPrinterConnected());
    printer.clear();
    printer.alignCenter();
    printer.bold(true);
    printer.setTextDoubleHeight();
    printer.setTextDoubleWidth();
    printer.drawLine();
    printer.println(name);
    printer.drawLine();
    printer.println(message);
    printer.drawLine();

    let imageStatus = 'not-printed';
    if (image) {
      const bytes = await image.arrayBuffer();
      const buffer = Buffer.from(bytes);
      try {
        const convertedBuffer = await sharp(buffer)
        .png({colors: 2})
        .resize(500,500)
        .toBuffer();
        await printer.printImageBuffer(convertedBuffer);
        imageStatus = 'printed';
      } catch (err) {
        console.error("Error converting image:", err);
        imageStatus = 'conversion-failed';
      }
    }
    printer.cut();
    await printer.execute();

    printer.clear();

    return NextResponse.json({ message: "Form submitted successfully", data: { name, message, image: imageStatus } });
  } catch (error) {
    console.error("Error processing form:", error);
    printer.clear();
    // newrelic.noticeError(error as Error);
    return NextResponse.json({ message: "Error processing form", error }, { status: 500 });
  }
}
